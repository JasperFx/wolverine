using System.Text;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// A listener with <c>DefaultIncomingMessage&lt;T&gt;()</c> stamps the message type before the durable inbox
/// stores the envelope, so the envelope is stored under its transport id and the CloudEvent is only read when
/// the handler pipeline calls ReadFromData. That read must not replace the id: marking the message handled
/// finds its inbox row by the id it was stored under.
/// </summary>
public class cloudevents_with_default_incoming_message_keeps_inbox_id
{
    private const string Schema = "kafka_ce_default_incoming";

    [Fact]
    public async Task a_handled_cloud_event_is_marked_handled_in_the_inbox()
    {
        var topic = $"ce-default-incoming-{Guid.NewGuid():N}";
        var cloudEventId = Guid.NewGuid();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.ListenToKafkaTopic(topic)
                    .InteropWithCloudEvents()
                    .DefaultIncomingMessage<CloudEventDefaultTypeMessage>()
                    .UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CloudEventDefaultTypeHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var cloudEvent = $$"""
            { "data": { "orderId": 7 }, "id": "{{cloudEventId}}", "specversion": "1.0",
              "datacontenttype": "application/json", "source": "integration-test", "type": "com.test.order.v1",
              "time": "2026-01-01T00:00:00Z" }
            """;

        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();
        await producer.ProduceAsync(topic,
            new Message<string, byte[]> { Key = "7", Value = Encoding.UTF8.GetBytes(cloudEvent) },
        TestContext.Current.CancellationToken);

        await CloudEventDefaultTypeHandler.Handled.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

        // The mark-handled write is not part of the handler's own commit, so give it a moment to land
        var statuses = new List<string>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        while (true)
        {
            statuses = await statusesAsync(conn, topic);
            if (statuses.Count == 0 || statuses.All(x => x == "Handled") || DateTimeOffset.UtcNow > deadline) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // Either retained as Handled or already deleted; never left Incoming
        statuses.ShouldNotContain("Incoming", $"Inbox statuses: {string.Join(", ", statuses)}");
    }

    private static async Task<List<string>> statusesAsync(NpgsqlConnection conn, string topic)
    {
        await using var cmd = new NpgsqlCommand(
            $"select status from {Schema}.wolverine_incoming_envelopes where received_at = @uri", conn);
        cmd.Parameters.AddWithValue("uri", $"kafka://topic/{topic}");
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) result.Add(reader.GetString(0));
        return result;
    }
}

public record CloudEventDefaultTypeMessage(int OrderId);

public static class CloudEventDefaultTypeHandler
{
    private static readonly TaskCompletionSource _handled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task Handled => _handled.Task;

    public static void Handle(CloudEventDefaultTypeMessage message) => _handled.TrySetResult();
}
