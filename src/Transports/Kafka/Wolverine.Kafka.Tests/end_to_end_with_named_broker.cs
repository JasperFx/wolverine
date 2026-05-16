using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.Kafka.Tests;

public class end_to_end_with_named_broker
{
    private readonly BrokerName brokerName = new("other");
    private const int MillisecondsTimeout = 30_000;

    [Fact]
    public async Task send_message_to_and_receive_through_kafka_with_inline_receivers()
    {
        var topicName = Guid.NewGuid().ToString();
        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString)
                .AutoProvision();

            opts.PublishAllMessages()
                .ToKafkaTopicOnNamedBroker(brokerName, topicName)
                .SendInline();
        });

        var callback = new TaskCompletionSource<ColorChosen>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString)
                .AutoProvision();
            opts.ListenToKafkaTopicOnNamedBroker(brokerName, topicName)
                .ProcessInline();
            opts.Services.AddSingleton(callback);

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<ColorHandler>();
        });
        var message = new ColorChosen { Name = Guid.NewGuid().ToString() };

        await publisher.SendAsync(message);

        var receivedMessage = await callback.Task
            .TimeoutAfterAsync(MillisecondsTimeout);
        receivedMessage.Name.ShouldBe(message.Name);
    }
}

public class ColorHandler
{
    public static void Handle(ColorChosen message, TaskCompletionSource<ColorChosen> callback)
    {
        callback.TrySetResult(message);
    }
}