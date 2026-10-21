using IntegrationTests;
using MySqlConnector;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

using Wolverine;
using Wolverine.Configuration;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;

namespace MySqlTests.Transport;

/// <summary>
/// The MySQL twin of https://github.com/JasperFx/wolverine/issues/5107. An envelope whose outbox row was
/// written by something other than the RDBMS message store -- the EF Core outbox, or outbox recovery reading
/// the row back -- does not carry <see cref="Envelope.WasPersistedInOutbox"/>, so the queue sender inserted it
/// into the queue in one transaction and left the outbox row for DurableSendingAgent to delete in a second one.
/// A node that died between the two left a row that outbox recovery sent again.
/// </summary>
[Collection("mysql")]
public class Bug_5107_outbox_row_survives_the_queue_insert : IAsyncLifetime
{
    private const string SchemaName = "wolverine_gh5107";
    private const string QueueName = "gh5107";

    private IHost _host = null!;
    private MySqlQueue _queue = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS {SchemaName}";
            await cmd.ExecuteNonQueryAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, SchemaName,
                    transportSchema: SchemaName);

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishMessage<Gh5107Message>().ToMySqlQueue(QueueName);
            }).StartAsync();

        _queue = _host.GetRuntime().Options.Transports.GetOrCreate<MySqlTransport>().Queues[QueueName];
        _queue.Mode = EndpointMode.Durable;
        await _queue.PurgeAsync(NullLogger.Instance);
        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Envelope> storeInTheOutboxAsync()
    {
        var runtime = _host.GetRuntime();
        var envelope = runtime.RoutingFor(typeof(Gh5107Message)).RouteForSend(new Gh5107Message(Guid.NewGuid()), null)
            .Single();

        // Owned by a node that is not this one, so neither this node's sending agent nor recovery touches it
        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, 9999);

        // What the EF Core outbox and outbox recovery hand the sender: the row is there, the flag is not
        envelope.WasPersistedInOutbox = false;

        return envelope;
    }

    private async Task<int> outboxCountAsync() => (await _host.GetRuntime().Storage.Admin.AllOutgoingAsync()).Count;

    [Fact]
    public async Task the_queue_insert_takes_the_outbox_row_with_it()
    {
        var envelope = await storeInTheOutboxAsync();

        await ((ISender)new MySqlQueueSender(_queue)).SendAsync(envelope);

        // Nothing has deleted the outbox row but the send itself
        (await outboxCountAsync()).ShouldBe(0);
        (await _queue.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task a_scheduled_send_takes_the_outbox_row_with_it()
    {
        var envelope = await storeInTheOutboxAsync();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);

        await ((ISender)new MySqlQueueSender(_queue)).SendAsync(envelope);

        (await outboxCountAsync()).ShouldBe(0);
        (await _queue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task an_envelope_with_no_outbox_row_is_still_written_to_the_queue()
    {
        var envelope = _host.GetRuntime().RoutingFor(typeof(Gh5107Message))
            .RouteForSend(new Gh5107Message(Guid.NewGuid()), null).Single();

        await ((ISender)new MySqlQueueSender(_queue)).SendAsync(envelope);

        (await _queue.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task sending_the_same_envelope_twice_is_still_one_queue_row()
    {
        var envelope = await storeInTheOutboxAsync();
        var sender = (ISender)new MySqlQueueSender(_queue);

        await sender.SendAsync(envelope);
        await sender.SendAsync(envelope);

        (await outboxCountAsync()).ShouldBe(0);
        (await _queue.CountAsync()).ShouldBe(1);
    }
}

public record Gh5107Message(Guid Id);
