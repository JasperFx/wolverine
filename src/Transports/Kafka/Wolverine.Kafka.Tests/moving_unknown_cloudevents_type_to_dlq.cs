using System.Text;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class moving_unknown_cloudevents_type_to_dlq : IAsyncLifetime
{
    private IHost _receiver;

    private readonly string _topicName = $"cloudevents-dlq-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);

                opts.ListenToKafkaTopic(_topicName)
                    .InteropWithCloudEvents();

                opts.Discovery.IncludeAssembly(GetType().Assembly);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka_ce_dlq");

                opts.Services.AddResourceSetupOnStartup();

                opts.Policies.UseDurableInboxOnAllListeners();

                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
            }).StartAsync();

        await _receiver.RebuildAllEnvelopeStorageAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task cloudevents_message_with_unknown_type_should_be_dead_lettered()
    {
        var cloudEventsJson = """
        {
          "data": { "orderId": 99 },
          "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
          "specversion": "1.0",
          "datacontenttype": "application/json; charset=utf-8",
          "source": "integration-test",
          "type": "com.test.unregistered.event.v1",
          "time": "2026-01-01T00:00:00Z"
        }
        """;

        var transport = _receiver.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();
        var producerBuilder = new ProducerBuilder<string, byte[]>(transport.ProducerConfig);
        using var producer = producerBuilder.Build();

        await producer.ProduceAsync(_topicName, new Message<string, byte[]>
        {
            Value = Encoding.UTF8.GetBytes(cloudEventsJson)
        });
        producer.Flush();

        // Poll until the message appears in the dead letter queue
        var storage = _receiver.GetRuntime().Storage;
        var deadline = DateTimeOffset.UtcNow.Add(2.Minutes());
        DeadLetterEnvelopeResults deadLetters = null!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            deadLetters = await storage.DeadLetters.QueryAsync(
                new DeadLetterEnvelopeQuery(TimeRange.AllTime()),
                CancellationToken.None);

            if (deadLetters.Envelopes.Any()) break;

            await Task.Delay(1.Seconds());
        }

        deadLetters.Envelopes.ShouldNotBeEmpty();
        var envelope = deadLetters.Envelopes.First();
        envelope.MessageType.ShouldBe("com.test.unregistered.event.v1");
        envelope.ExceptionType.ShouldContain("UnknownMessageTypeNameException");
    }
}
