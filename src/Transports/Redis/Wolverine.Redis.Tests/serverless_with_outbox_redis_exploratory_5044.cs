using System.Collections.Concurrent;
using System.Reflection;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Postgresql;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
///     PR #5044 exploratory: how the Redis stream sender protocol reports broker acceptance to the
///     ServerlessWithOutbox inline send and the durability pass. Every key is prefixed explore5044 because
///     the Redis this runs against may be shared
/// </summary>
public class serverless_with_outbox_redis_exploratory_5044 : IAsyncLifetime
{
    private const string Schema = "explore5044_redis";
    private readonly string _streamKey = $"explore5044-redis-{Guid.NewGuid():N}";
    private IHost _host = null!;
    private Explore5044RedisCountingProtocol _counting = null!;
    private IDatabase _database = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Uri StreamUri => new($"redis://stream/0/{_streamKey}");

    private Task<IHost> buildHostAsync(string redisConnectionString)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044RedisHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.UseRedisTransport(redisConnectionString);
                opts.PublishMessage<Explore5044RedisMessage>().ToRedisStream(_streamKey).UseDurableOutbox();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);
    }

    public async ValueTask InitializeAsync()
    {
        _host = await buildHostAsync(RedisContainerFixture.ConnectionString);

        // Not ResetResourceState: that would also touch the Redis side, which may be shared
        await _host.GetRuntime().Storage.Admin.ClearAllAsync();

        _counting = Explore5044RedisCountingProtocol.Wrap(_host, StreamUri);
        _database = _host.GetRuntime().Options.Transports.GetOrCreate<RedisTransport>().GetDatabase(database: 0);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.KeyDeleteAsync(_streamKey);
        await _database.KeyDeleteAsync($"{_streamKey}:scheduled");
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select id, owner_id from {Schema}.wolverine_outgoing_envelopes";
        var rows = new Dictionary<Guid, int>();
        await using var reader = await cmd.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token)) rows[reader.GetGuid(0)] = reader.GetInt32(1);
        return rows;
    }

    private async Task makeOutboxStaleAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"update {Schema}.wolverine_outgoing_envelopes set \"timestamp\" = now() - interval '2 hours'";
        await cmd.ExecuteNonQueryAsync(Token);
    }

    private async Task<Envelope> storeOutboxRowAsync(int number, DateTimeOffset? scheduledTime = null)
    {
        var runtime = _host.GetRuntime();
        var envelope = new Envelope(new Explore5044RedisMessage(number))
        {
            Destination = StreamUri,
            ContentType = "application/json",
            MessageType = typeof(Explore5044RedisMessage).ToMessageTypeName(),
            ScheduledTime = scheduledTime
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, 0);
        return envelope;
    }

    [Fact]
    public async Task happy_path_sends_one_batch_and_leaves_no_outbox_rows()
    {
        await _host.MessageBus().InvokeAsync(new Explore5044RedisTrigger(5), Token);

        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });
        (await _database.StreamLengthAsync(_streamKey)).ShouldBe(5);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_pass_sends_recovered_rows_in_one_batch_and_deletes_them()
    {
        for (var i = 0; i < 5; i++) await storeOutboxRowAsync(i);

        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });
        (await _database.StreamLengthAsync(_streamKey)).ShouldBe(5);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task one_failing_command_in_a_batch_fails_the_commands_that_succeeded()
    {
        // Two immediate rows and one scheduled one, whose ZADD is made to fail by giving the scheduled key the
        // wrong type. The XADDs of the two immediate rows succeed
        await storeOutboxRowAsync(1);
        await storeOutboxRowAsync(2);
        await storeOutboxRowAsync(3, DateTimeOffset.UtcNow.AddHours(1));
        await _database.StringSetAsync($"{_streamKey}:scheduled", "explore5044-wrong-type");

        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        var streamAfterFirst = await _database.StreamLengthAsync(_streamKey);
        var rowsAfterFirst = await outboxRowsAsync();

        streamAfterFirst.ShouldBe(2);

        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        var streamAfterSecond = await _database.StreamLengthAsync(_streamKey);

        // EXPECTED: only the scheduled row, which Redis refused, is left for the next pass
        rowsAfterFirst.Count.ShouldBe(1,
            $"after the first pass the stream held {streamAfterFirst} entries but {rowsAfterFirst.Count} outbox rows were left; after a second pass the stream holds {streamAfterSecond} entries");
    }

    [Fact]
    public async Task redis_rejecting_every_command_leaves_every_row_and_a_pass_delivers_them_later()
    {
        // Redis refuses every XADD because the stream key has the wrong type. Stands in for a broker that
        // will not take anything (the host cannot even start against a Redis that is down)
        await _database.StringSetAsync(_streamKey, "explore5044-wrong-type");

        await _host.MessageBus().InvokeAsync(new Explore5044RedisTrigger(3), Token);

        var rows = await outboxRowsAsync();
        rows.Count.ShouldBe(3);
        rows.Values.ShouldAllBe(x => x == 0);

        // Fixed, then a pass delivers them exactly once
        await _database.KeyDeleteAsync(_streamKey);
        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        (await _database.StreamLengthAsync(_streamKey)).ShouldBe(3);
        (await outboxRowsAsync()).ShouldBeEmpty();
        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 3, 3 });
    }
}

public record Explore5044RedisTrigger(int Count);

public record Explore5044RedisMessage(int Number);

public static class Explore5044RedisHandler
{
    public static OutgoingMessages Handle(Explore5044RedisTrigger trigger)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < trigger.Count; i++) messages.Add(new Explore5044RedisMessage(i));
        return messages;
    }
}

internal class Explore5044RedisCountingProtocol : ISenderProtocol
{
    private readonly ISenderProtocol _inner;

    public Explore5044RedisCountingProtocol(ISenderProtocol inner) => _inner = inner;

    public ConcurrentQueue<Envelope[]> Calls { get; } = new();
    public List<Envelope[]> Batches => Calls.ToList();

    public Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        Calls.Enqueue(batch.Messages.ToArray());
        return _inner.SendBatchAsync(callback, batch);
    }

    public static Explore5044RedisCountingProtocol Wrap(IHost host, Uri uri)
    {
        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(uri);
        var sender = (ISender)agent.GetType().GetProperty("Sender")!.GetValue(agent)!;
        var batched = sender.ShouldBeOfType<BatchedSender>();
        var field = typeof(BatchedSender).GetField("_protocol", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counting = new Explore5044RedisCountingProtocol((ISenderProtocol)field.GetValue(batched)!);
        field.SetValue(batched, counting);
        return counting;
    }
}
