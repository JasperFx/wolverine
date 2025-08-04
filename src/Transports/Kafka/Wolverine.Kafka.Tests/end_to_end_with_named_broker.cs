using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class end_to_end_with_named_broker
{
    private readonly ITestOutputHelper _output;
    private readonly BrokerName theName = new BrokerName("other");

    public end_to_end_with_named_broker(ITestOutputHelper output)
    {
        _output = output;
    }
    
    [Fact]
    public async Task send_message_to_and_receive_through_kafka_with_inline_receivers()
    {
        var topicName = Guid.NewGuid().ToString();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.AddNamedKafkaBroker(theName, "localhost:9092").AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToKafkaTopicOnNamedBroker(theName, topicName)
                .SendInline();
        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.AddNamedKafkaBroker(theName, "localhost:9092").AutoProvision();

            opts.ListenToKafkaTopicOnNamedBroker(theName, topicName).ProcessInline().Named(topicName);
            opts.Services.AddSingleton<ColorHistory>();

        });

        ColorHandler.Received = new();

        Task.Run(async () =>
        {
            for (int i = 0; i < 10000; i++)
            {
                await publisher.SendAsync(new ColorChosen { Name = "blue" });
            }
        });
        


        await ColorHandler.Received.Task.TimeoutAfterAsync(10000);        
    }
    
}

public record RequestId(Guid Id);
public record ResponseId(Guid Id);

public static class RequestIdHandler
{
    public static ResponseId Handle(RequestId message) => new ResponseId(message.Id);
}

public class ColorHandler
{
    public void Handle(ColorChosen message, ColorHistory history, Envelope envelope)
    {
        history.Name = message.Name;
        history.Envelope = envelope;

        Received.TrySetResult(true);
    }

    public static TaskCompletionSource<bool> Received { get; set; } = new();
}