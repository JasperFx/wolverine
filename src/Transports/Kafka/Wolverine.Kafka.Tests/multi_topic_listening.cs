using System.Diagnostics;
using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class multi_topic_listening : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender = null!;
    private IHost _receiver = null!;

    public multi_topic_listening(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c =>
                    {
                        c.AutoOffsetReset = AutoOffsetReset.Earliest;
                        c.GroupId = "multi-topic-test";
                    });

                // Listen to both topics with a single consumer
                opts.ListenToKafkaTopics("multi-alpha", "multi-beta")
                    .ProcessInline();

                opts.ServiceName = "multi-topic-receiver";

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

                opts.ServiceName = "multi-topic-sender";

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
            }).StartAsync();
    }

    [Fact]
    public async Task receive_from_multiple_topics_with_single_consumer()
    {
        MultiTopicAlphaHandler.Received = new TaskCompletionSource<bool>();
        MultiTopicBetaHandler.Received = new TaskCompletionSource<bool>();

        await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<AlphaMessage>(_receiver)
            .PublishMessageAndWaitAsync(new AlphaMessage("hello"));

        await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<BetaMessage>(_receiver)
            .PublishMessageAndWaitAsync(new BetaMessage("world"));

        await MultiTopicAlphaHandler.Received.Task.TimeoutAfterAsync(30000);
        await MultiTopicBetaHandler.Received.Task.TimeoutAfterAsync(30000);
    }

    [Fact]
    public async Task topic_group_uri_uses_concatenated_names()
    {
        var runtime = _receiver.Services.GetRequiredService<IWolverineRuntime>();
        var endpoints = runtime.Options.Transports.SelectMany(t => t.Endpoints()).ToArray();

        // Should find the topic group endpoint with concatenated name
        var groupEndpoint = endpoints
            .OfType<KafkaTopicGroup>()
            .FirstOrDefault();

        groupEndpoint.ShouldNotBeNull();
        groupEndpoint.TopicNames.ShouldContain("multi-alpha");
        groupEndpoint.TopicNames.ShouldContain("multi-beta");
        groupEndpoint.Uri.ToString().ShouldContain("topic/");
    }

    [Fact]
    public async Task received_message_from_topic_group_has_partition_id_header()
    {
        MultiTopicAlphaHandler.Received = new TaskCompletionSource<bool>();

        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<AlphaMessage>(_receiver)
            .PublishMessageAndWaitAsync(new AlphaMessage("partition-check"));

        await MultiTopicAlphaHandler.Received.Task.TimeoutAfterAsync(30000);

        var envelope = session.Received.SingleEnvelope<AlphaMessage>();
        envelope.TryGetHeader("kafka-partition-id", out var partitionIdValue).ShouldBeTrue();
        int.TryParse(partitionIdValue, out var partitionId).ShouldBeTrue();
        partitionId.ShouldBeGreaterThanOrEqualTo(0);
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }
}

[Topic("multi-alpha")]
public record AlphaMessage(string Text);

[Topic("multi-beta")]
public record BetaMessage(string Text);

public static class MultiTopicAlphaHandler
{
    public static TaskCompletionSource<bool> Received { get; set; } = new();

    public static void Handle(AlphaMessage message)
    {
        Debug.WriteLine("Got alpha: " + message.Text);
        Received.TrySetResult(true);
    }
}

public static class MultiTopicBetaHandler
{
    public static TaskCompletionSource<bool> Received { get; set; } = new();

    public static void Handle(BetaMessage message)
    {
        Debug.WriteLine("Got beta: " + message.Text);
        Received.TrySetResult(true);
    }
}
