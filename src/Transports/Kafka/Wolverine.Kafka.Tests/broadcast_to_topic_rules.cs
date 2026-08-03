using System.Diagnostics;
using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;
namespace Wolverine.Kafka.Tests;

public class broadcast_to_topic_rules : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender = null!;
    private IHost _receiver = null!;

    public broadcast_to_topic_rules(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);
                // Dedicated topics and consumer groups. RedMessage/GreenMessage and the topics
                // "red"/"green"/"blue" are also used by configure_consumers_and_publishers, and
                // "green"/"blue" fell into the DEFAULT group ("Wolverine.Kafka.Tests") that every
                // other class in this assembly shares. Bobcat runs this project across worker
                // PROCESSES partitioned by class, so those two classes consume the same topics in
                // the same group concurrently and rebalance each other out mid-test. That is what
                // "Flaky in CI due to Kafka consumer group timing issues" was half of.
                //
                // The other half: ExtendConsumerConfiguration, NOT ConfigureConsumer.
                // ConfigureConsumer builds a new ConsumerConfig and replaces the parent's outright
                // -- documented, but it meant the ConfigureConsumers(AutoOffsetReset.Earliest)
                // above was silently discarded for any topic that set a GroupId. A brand-new
                // consumer group then defaulted to Latest, so on a fresh broker the message was
                // published before the group finished joining and was never delivered at all.
                // GH-3763.
                opts.ListenToKafkaTopic(BroadcastRulesTopics.Red)
                    .ExtendConsumerConfiguration(c => c.GroupId = "crimson");
                opts.ListenToKafkaTopic(BroadcastRulesTopics.Green)
                    .ExtendConsumerConfiguration(c => c.GroupId = "broadcast-rules-green");
                opts.ListenToKafkaTopic(BroadcastRulesTopics.Blue)
                    .ExtendConsumerConfiguration(c => c.GroupId = "broadcast-rules-blue");
                opts.ListenToKafkaTopic(BroadcastRulesTopics.Purple)
                    .ExtendConsumerConfiguration(c => c.GroupId = "broadcast-rules-purple");

                opts.ServiceName = "receiver";

                // Include test assembly for handler discovery
                opts.Discovery.IncludeAssembly(GetType().Assembly);

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
            }).StartAsync();

        
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishAllMessages().ToKafkaTopics().SendInline();

                opts.ServiceName = "sender";

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
            }).StartAsync();
    }

    [Fact]
    public async Task route_by_derived_topics_1()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<BroadcastRedMessage>(_receiver)
            .PublishMessageAndWaitAsync(new BroadcastRedMessage("one"));

        var singleEnvelope = session.Received.SingleEnvelope<BroadcastRedMessage>();
        singleEnvelope
            .Destination.ShouldBe(new Uri($"kafka://topic/{BroadcastRulesTopics.Red}"));

        singleEnvelope.GroupId.ShouldBe("crimson");
    }

    [Fact]
    public async Task route_by_derived_topics_2()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<BroadcastGreenMessage>(_receiver)
            .PublishMessageAndWaitAsync(new BroadcastGreenMessage("one"));

        session.Received.SingleEnvelope<BroadcastGreenMessage>()
            .Destination.ShouldBe(new Uri($"kafka://topic/{BroadcastRulesTopics.Green}"));
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }
}

/// <summary>
/// Topic names owned by broadcast_to_topic_rules alone, so nothing else in this assembly
/// publishes to them or joins a consumer group on them. See GH-3763.
/// </summary>
public static class BroadcastRulesTopics
{
    public const string Red = "broadcast-rules-red";
    public const string Green = "broadcast-rules-green";
    public const string Blue = "broadcast-rules-blue";
    public const string Purple = "broadcast-rules-purple";
}

[Topic(BroadcastRulesTopics.Red)]
public record BroadcastRedMessage(string Name);

[Topic(BroadcastRulesTopics.Green)]
public record BroadcastGreenMessage(string Name);

[Topic("red")]
public record RedMessage(string Name);

[Topic("green")]
public record GreenMessage(string Name);

public static class ColoredMessageHandler
{
    public static void Handle(RedMessage m) => Debug.WriteLine("Got red " + m.Name);
    public static void Handle(GreenMessage m) => Debug.WriteLine("Got green " + m.Name);
    public static void Handle(BroadcastRedMessage m) => Debug.WriteLine("Got red " + m.Name);
    public static void Handle(BroadcastGreenMessage m) => Debug.WriteLine("Got green " + m.Name);
}
