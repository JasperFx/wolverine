using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Confluent.Kafka;
using IntegrationTests;
using Wolverine.Util;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka.Tests;

/// <summary>
///     PR #5044: how the Kafka sender protocol reports broker acceptance to the
///     ServerlessWithOutbox inline send and the durability pass
/// </summary>
public class serverless_with_outbox_kafka_5044 : IAsyncLifetime
{
    private const string Schema = "serverless5044_kafka";
    private readonly string _topic = $"serverless5044-kafka-{Guid.NewGuid():N}";
    private IHost _host = null!;
    private Serverless5044CountingProtocol _counting = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Serverless5044KafkaHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                // Short delivery timeout so a failure does not hang for the producer's default
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision()
                    .ConfigureProducerBuilders(b => b.WithDeliveryTimeout(10.Seconds()));

                opts.PublishMessage<Serverless5044KafkaMessage>().ToKafkaTopic(_topic).UseDurableOutbox();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await _host.ResetResourceState();

        _counting = Serverless5044CountingProtocol.Wrap(_host, new Uri($"kafka://topic/{_topic}"));
    }

    public async ValueTask DisposeAsync()
    {
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

    // Every record on the topic, by Wolverine envelope id header, read with a plain consumer
    private List<string> recordsOnTopic(int expectedAtLeast, TimeSpan wait)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = $"serverless5044-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(_topic);

        var ids = new List<string>();
        var deadline = DateTimeOffset.UtcNow.Add(wait);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = consumer.Consume(500.Milliseconds());
            if (result?.Message == null) continue;
            var header = result.Message.Headers.FirstOrDefault(x => x.Key == "id");
            ids.Add(header == null ? "?" : Encoding.UTF8.GetString(header.GetValueBytes()));
            if (ids.Count >= expectedAtLeast) deadline = DateTimeOffset.UtcNow.Add(2.Seconds());
        }

        consumer.Close();
        return ids;
    }

    [Fact]
    public async Task happy_path_sends_one_batch_and_leaves_no_outbox_rows()
    {
        await _host.MessageBus().InvokeAsync(new Serverless5044KafkaTrigger(5, -1), Token);

        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });

        var records = recordsOnTopic(5, 20.Seconds());
        records.Count.ShouldBe(5);
        records.Distinct().Count().ShouldBe(5);

        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task one_oversized_record_in_an_inline_batch()
    {
        // 4 ordinary records and one over the producer's 1MB message.max.bytes, all in one inline batch
        await _host.MessageBus().InvokeAsync(new Serverless5044KafkaTrigger(5, 2), Token);

        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });

        var firstSend = recordsOnTopic(4, 15.Seconds());
        var rowsAfterInline = await outboxRowsAsync();

        // Observed: the broker acked the 4 ordinary records...
        firstSend.Count.ShouldBe(4);

        // ...and every row is owned by any node, so a pass recovers them
        rowsAfterInline.Values.ShouldAllBe(x => x == 0);

        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        var afterPass = recordsOnTopic(8, 15.Seconds());

        // A second pass, to see whether the duplicates keep growing while the oversized row stays
        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        var afterSecondPass = recordsOnTopic(12, 15.Seconds());
        var rowsAfterPasses = await outboxRowsAsync();

        // EXPECTED (no duplicates): only the oversized message should have been left in the outbox, so a
        // pass cannot re-send the 4 records the broker already acked
        rowsAfterInline.Count.ShouldBe(1,
            $"outbox kept {rowsAfterInline.Count} rows after the inline send although the broker acked {firstSend.Count}; after one pass the topic holds {afterPass.Count} records ({afterPass.Count - afterPass.Distinct().Count()} duplicates), after two passes {afterSecondPass.Count} records with {rowsAfterPasses.Count} rows still in the outbox");
    }

    [Fact]
    public async Task the_pass_keeps_resending_an_oversized_record_forever()
    {
        await _host.MessageBus().InvokeAsync(new Serverless5044KafkaTrigger(1, 0), Token);

        (await outboxRowsAsync()).Count.ShouldBe(1);

        for (var i = 0; i < 3; i++)
        {
            await makeOutboxStaleAsync();
            await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        }

        // Observed: the oversized row is never discarded, every pass retries it. Recorded as a fact
        (await outboxRowsAsync()).Count.ShouldBe(1);
        _counting.Batches.Count.ShouldBe(4);
    }

    [Fact]
    public async Task the_pass_sends_recovered_rows_in_one_batch_and_deletes_them()
    {
        // Rows as a failed inline send leaves them
        var runtime = _host.GetRuntime();
        var uri = new Uri($"kafka://topic/{_topic}");
        for (var i = 0; i < 5; i++)
        {
            var envelope = new Envelope(new Serverless5044KafkaMessage(i, "x"))
            {
                Destination = uri, ContentType = "application/json",
                MessageType = typeof(Serverless5044KafkaMessage).ToMessageTypeName()
            };
            envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
            await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, 0);
        }

        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });
        recordsOnTopic(5, 20.Seconds()).Count.ShouldBe(5);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

public record Serverless5044KafkaTrigger(int Count, int OversizedIndex);

public record Serverless5044KafkaMessage(int Number, string Payload);

public static class Serverless5044KafkaHandler
{
    public static OutgoingMessages Handle(Serverless5044KafkaTrigger trigger)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < trigger.Count; i++)
        {
            var payload = i == trigger.OversizedIndex ? new string('x', 1_500_000) : "small";
            messages.Add(new Serverless5044KafkaMessage(i, payload));
        }

        return messages;
    }
}

/// <summary>
///     Wraps a BatchedSender's ISenderProtocol to count broker round trips
/// </summary>
internal class Serverless5044CountingProtocol : ISenderProtocol
{
    private readonly ISenderProtocol _inner;

    public Serverless5044CountingProtocol(ISenderProtocol inner) => _inner = inner;

    public ConcurrentQueue<Envelope[]> Calls { get; } = new();
    public List<Envelope[]> Batches => Calls.ToList();

    public Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        Calls.Enqueue(batch.Messages.ToArray());
        return _inner.SendBatchAsync(callback, batch);
    }

    public static Serverless5044CountingProtocol Wrap(IHost host, Uri uri)
    {
        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(uri);
        var sender = (ISender)agent.GetType().GetProperty("Sender")!.GetValue(agent)!;
        return Wrap(sender.ShouldBeOfType<BatchedSender>());
    }

    public static Serverless5044CountingProtocol Wrap(BatchedSender sender)
    {
        var field = typeof(BatchedSender).GetField("_protocol", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counting = new Serverless5044CountingProtocol((ISenderProtocol)field.GetValue(sender)!);
        field.SetValue(sender, counting);
        return counting;
    }
}
