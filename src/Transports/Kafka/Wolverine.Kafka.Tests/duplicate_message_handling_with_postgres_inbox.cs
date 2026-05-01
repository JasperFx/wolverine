using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// End-to-end coverage that a duplicate envelope id arriving over the real Kafka
/// transport, against a real Postgres durable inbox, is silently discarded and the
/// partition keeps flowing. This exercises the single-envelope path
/// (KafkaListener -> DurableReceiver.receiveOneAsync -> single StoreIncomingAsync),
/// which is the path Kafka actually uses today, and confirms the SqlState-based
/// duplicate detection works locale-independently against a live driver.
///
/// It does NOT exercise the batch-insert path -- that is covered by the
/// MessageStoreCompliance tests for each RDBMS backend.
/// </summary>
[Trait("Category", "Flaky")]
public class duplicate_message_handling_with_postgres_inbox : IAsyncLifetime
{
    private IHost _host = null!;
    private string _topicName = null!;

    public async Task InitializeAsync()
    {
        DupTestHandler.Reset();

        _topicName = $"dup-test-{Guid.NewGuid():N}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka_dup_test");

                opts.ListenToKafkaTopic(_topicName).UseDurableInbox();

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task duplicate_message_is_discarded_and_partition_continues()
    {
        var fixedId = Guid.NewGuid();
        var freshId = Guid.NewGuid();

        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();
        var producerBuilder = new ProducerBuilder<string, byte[]>(transport.ProducerConfig);
        using var producer = producerBuilder.Build();

        await ProduceAsync(producer, _topicName, fixedId, new DupTestMessage("first"));
        await ProduceAsync(producer, _topicName, fixedId, new DupTestMessage("duplicate"));
        await ProduceAsync(producer, _topicName, freshId, new DupTestMessage("third"));
        producer.Flush();

        // Wait until both unique envelope IDs have been processed by the handler.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (DupTestHandler.HandledIds.Contains(fixedId)
                && DupTestHandler.HandledIds.Contains(freshId))
            {
                break;
            }

            await Task.Delay(250);
        }

        DupTestHandler.HandledIds.ShouldContain(fixedId);
        DupTestHandler.HandledIds.ShouldContain(freshId);

        // The handler must run exactly once for the fixedId — the duplicate is discarded.
        DupTestHandler.HandledIdHistory.Count(id => id == fixedId).ShouldBe(1);

        // Bodies seen: "first" and "third" — never "duplicate".
        DupTestHandler.HandledBodies.ShouldContain("first");
        DupTestHandler.HandledBodies.ShouldContain("third");
        DupTestHandler.HandledBodies.ShouldNotContain("duplicate");
    }

    private static Task<DeliveryResult<string, byte[]>> ProduceAsync(
        IProducer<string, byte[]> producer,
        string topic,
        Guid envelopeId,
        DupTestMessage payload)
    {
        var headers = new Headers
        {
            { "id", Encoding.UTF8.GetBytes(envelopeId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(typeof(DupTestMessage).ToMessageTypeName()) },
            { "content-type", Encoding.UTF8.GetBytes("application/json") }
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);

        return producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = envelopeId.ToString(),
            Value = body,
            Headers = headers
        });
    }
}

public class DupTestMessage
{
    public DupTestMessage()
    {
    }

    public DupTestMessage(string body)
    {
        Body = body;
    }

    public string Body { get; set; } = null!;
}

public static class DupTestHandler
{
    private static readonly object _lock = new();
    private static readonly List<Guid> _handledIdHistory = new();
    private static readonly HashSet<Guid> _handledIds = new();
    private static readonly List<string> _handledBodies = new();

    public static IReadOnlyList<Guid> HandledIdHistory
    {
        get { lock (_lock) { return _handledIdHistory.ToArray(); } }
    }

    public static IReadOnlyCollection<Guid> HandledIds
    {
        get { lock (_lock) { return _handledIds.ToArray(); } }
    }

    public static IReadOnlyList<string> HandledBodies
    {
        get { lock (_lock) { return _handledBodies.ToArray(); } }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _handledIdHistory.Clear();
            _handledIds.Clear();
            _handledBodies.Clear();
        }
    }

    public static void Handle(DupTestMessage message, Envelope envelope)
    {
        lock (_lock)
        {
            _handledIdHistory.Add(envelope.Id);
            _handledIds.Add(envelope.Id);
            _handledBodies.Add(message.Body);
        }
    }
}
