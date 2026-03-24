using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class send_kafka_tombstone : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender = null!;

    public send_kafka_tombstone(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task send_tombstone_produces_message_with_null_value()
    {
        var topicName = "tombstone-test-" + Guid.NewGuid().ToString("N")[..8];
        var tombstoneKey = "record-to-delete";

        var bus = _sender.MessageBus();
        await bus.BroadcastToTopicAsync(topicName, new KafkaTombstone(tombstoneKey));

        // Give the batched sender time to flush
        await Task.Delay(5.Seconds());

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = "tombstone-verifier-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        consumer.Subscribe(topicName);

        var result = consumer.Consume(TimeSpan.FromSeconds(15));

        consumer.Close();

        result.ShouldNotBeNull("Tombstone message was not found on the Kafka topic");
        result.Message.Key.ShouldBe(tombstoneKey);
        result.Message.Value.ShouldBeNull();
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
    }
}
