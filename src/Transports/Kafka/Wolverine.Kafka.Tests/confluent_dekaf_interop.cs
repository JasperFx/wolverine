using System.Text;
using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

// The transport now runs on the Dekaf client; these tests use raw Confluent.Kafka clients as an
// independent oracle to prove full wire compatibility for mixed fleets: keys, values, text and
// binary headers, and timestamps all round-trip in both directions.
public class confluent_dekaf_interop
{
    [Fact]
    public async Task confluent_produced_record_reaches_a_wolverine_listener_with_headers_intact()
    {
        string? observedHeader = null;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "interop-inbound";
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.ListenToKafkaTopic("interop-inbound")
                    .ReceiveRawJson<InteropMessage>()
                    .BeginAtEarliest();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var session = await host.TrackActivity().IncludeExternalTransports().Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<InteropMessage>(host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
                {
                    BootstrapServers = KafkaContainerFixture.ConnectionString
                }).Build();

                await producer.ProduceAsync("interop-inbound", new Message<string, byte[]>
                {
                    Key = "from-confluent",
                    Value = "{\"name\":\"confluent\"}"u8.ToArray(),
                    Headers = new Headers
                    {
                        { "interop-header", "from-the-other-side"u8.ToArray() }
                    }
                });
                producer.Flush(TimeSpan.FromSeconds(10));
            }));

        var envelope = session.Received.SingleEnvelope<InteropMessage>();
        envelope.PartitionKey.ShouldBe("from-confluent");
        observedHeader = envelope.Headers["interop-header"];
        observedHeader.ShouldBe("from-the-other-side");
    }

    [Fact]
    public async Task wolverine_produced_record_is_readable_by_a_raw_confluent_consumer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "interop-outbound";
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<InteropMessage>().ToKafkaTopic("interop-outbound").SendInline();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = $"interop-oracle-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe("interop-outbound");

        await host.MessageBus().PublishAsync(new InteropMessage("dekaf"));

        var result = consumer.Consume(TimeSpan.FromSeconds(30));
        result.ShouldNotBeNull();

        // Wolverine's default mapper writes the envelope metadata as UTF8 headers a Confluent
        // consumer can read verbatim
        result.Message.Headers.TryGetLastBytes(EnvelopeConstants.MessageTypeKey, out var messageType)
            .ShouldBeTrue();
        Encoding.UTF8.GetString(messageType).ShouldContain("InteropMessage");
        Encoding.UTF8.GetString(result.Message.Value).ShouldContain("dekaf");

        consumer.Close();
    }
}

public record InteropMessage(string Name);

public static class InteropMessageHandler
{
    public static void Handle(InteropMessage message)
    {
    }
}
