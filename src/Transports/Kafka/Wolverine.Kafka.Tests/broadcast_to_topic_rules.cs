using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class broadcast_to_topic_rules : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public broadcast_to_topic_rules(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.ListenToKafkaTopic("red");
                opts.ListenToKafkaTopic("green");
                opts.ListenToKafkaTopic("blue");
                opts.ListenToKafkaTopic("purple");

                opts.ServiceName = "receiver";

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishAllMessages().ToKafkaTopics();

                opts.ServiceName = "sender";

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task route_by_derived_topics_1()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<RedMessage>(_receiver)
            .PublishMessageAndWaitAsync(new RedMessage("one"));

        session.Received.SingleEnvelope<RedMessage>()
            .Destination.ShouldBe(new Uri("kafka://topic/red"));
    }

    [Fact]
    public async Task route_by_derived_topics_2()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<GreenMessage>(_receiver)
            .PublishMessageAndWaitAsync(new GreenMessage("one"));

        session.Received.SingleEnvelope<GreenMessage>()
            .Destination.ShouldBe(new Uri("kafka://topic/green"));
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}

[Topic("red")]
public record RedMessage(string Name);

[Topic("green")]
public record GreenMessage(string Name);

public static class ColoredMessageHandler
{
    public static void Handle(RedMessage m) => Debug.WriteLine("Got red " + m.Name);
    public static void Handle(GreenMessage m) => Debug.WriteLine("Got green " + m.Name);
}