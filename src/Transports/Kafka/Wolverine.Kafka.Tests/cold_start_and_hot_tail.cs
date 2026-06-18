using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// GH-3146: cold-start AutoOffsetReset + ephemeral hot-tail/broadcast consume.
public class cold_start_and_hot_tail
{
    private readonly BrokerName brokerName = new("coldhot3146");

    private static KafkaTopic applyListener(Action<KafkaListenerConfiguration> configure)
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["events"];
        var config = new KafkaListenerConfiguration(topic);
        configure(config);
        ((IDelayedEndpointConfiguration)config).Apply();
        return topic;
    }

    // ---- config unit tests (no broker) ----

    [Fact]
    public void listener_begin_at_earliest()
    {
        var topic = applyListener(c => c.BeginAtEarliest());
        topic.ConsumerConfig!.AutoOffsetReset.ShouldBe(AutoOffsetReset.Earliest);
    }

    [Fact]
    public void listener_begin_at_latest()
    {
        var topic = applyListener(c => c.BeginAtLatest());
        topic.ConsumerConfig!.AutoOffsetReset.ShouldBe(AutoOffsetReset.Latest);
    }

    [Fact]
    public void tail_from_latest_marks_hot_tail_and_latest()
    {
        var topic = applyListener(c => c.TailFromLatest());
        topic.IsHotTail.ShouldBeTrue();
        topic.ConsumerConfig!.AutoOffsetReset.ShouldBe(AutoOffsetReset.Latest);
    }

    [Fact]
    public void transport_begin_at_earliest_and_latest()
    {
        var transport = new KafkaTransport();
        var expr = new KafkaTransportExpression(transport, new WolverineOptions());

        expr.BeginAtEarliest();
        transport.ConsumerConfig.AutoOffsetReset.ShouldBe(AutoOffsetReset.Earliest);

        expr.BeginAtLatest();
        transport.ConsumerConfig.AutoOffsetReset.ShouldBe(AutoOffsetReset.Latest);
    }

    // ---- end-to-end (Kafka docker) ----

    private Task<IHost> publisherAsync(string topic) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.PublishAllMessages().ToKafkaTopicOnNamedBroker(brokerName, topic).SendInline();
    });

    private Task<IHost> hotTailNodeAsync(string topic) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.ListenToKafkaTopicOnNamedBroker(brokerName, topic).ProcessInline().TailFromLatest();
        opts.Services.AddSingleton<NodeSink>();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<HotTailHandler>();
    });

    [Fact]
    public async Task hot_tail_delivers_all_messages_to_every_node()
    {
        var topic = Guid.NewGuid().ToString();

        using var publisher = await publisherAsync(topic);
        using var nodeA = await hotTailNodeAsync(topic);
        using var nodeB = await hotTailNodeAsync(topic);

        // Each node should have joined a unique, ephemeral hot-tail group with no commits.
        string? GroupIdFor(IHost node)
        {
            var listener = node.GetRuntime().Endpoints.ActiveListeners()
                .Single(x => x.Uri.ToString().Contains(topic)).ShouldBeOfType<ListeningAgent>()
                .Listener.ShouldBeOfType<KafkaListener>();
            listener.Config.GroupId.ShouldNotBeNull();
            listener.Config.GroupId.ShouldContain("hot-tail");
            listener.Config.EnableAutoCommit.ShouldBe(true);
            return listener.Config.GroupId;
        }

        var groupA = GroupIdFor(nodeA);
        var groupB = GroupIdFor(nodeB);
        groupA.ShouldNotBe(groupB);

        // Latest means only messages published AFTER the consumers join are seen — give them a moment
        // to be assigned their partitions before publishing.
        await Task.Delay(3000);

        for (var i = 0; i < 5; i++)
        {
            await publisher.SendAsync(new HotTailMessage { Id = "m-" + i });
        }

        var sinkA = nodeA.Services.GetRequiredService<NodeSink>();
        var sinkB = nodeB.Services.GetRequiredService<NodeSink>();
        await waitForCountAsync(sinkA.Received, 5);
        await waitForCountAsync(sinkB.Received, 5);

        // Both nodes (each its own group) received all five messages.
        sinkA.Received.OrderBy(x => x).ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4"]);
        sinkB.Received.OrderBy(x => x).ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4"]);
    }

    [Fact]
    public async Task cold_start_at_earliest_replays_from_the_beginning()
    {
        var topic = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        using var publisher = await publisherAsync(topic);

        // Publish BEFORE any consumer exists for this group.
        for (var i = 0; i < 4; i++)
        {
            await publisher.SendAsync(new HotTailMessage { Id = "pre-" + i });
        }

        // A fresh group starting at earliest should replay everything already on the topic.
        using var consumer = await WolverineHost.ForAsync(opts =>
        {
            opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.ListenToKafkaTopicOnNamedBroker(brokerName, topic)
                .ProcessInline()
                .BeginAtEarliest()
                .ConfigureConsumer(x =>
                {
                    x.GroupId = groupId;
                    x.AutoOffsetReset = AutoOffsetReset.Earliest;
                });
            opts.Services.AddSingleton<NodeSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<HotTailHandler>();
        });

        var sink = consumer.Services.GetRequiredService<NodeSink>();
        await waitForCountAsync(sink.Received, 4);
        sink.Received.OrderBy(x => x).ShouldBe(["pre-0", "pre-1", "pre-2", "pre-3"]);
    }

    private static async Task waitForCountAsync(ConcurrentBag<string> bag, int count, int timeoutMs = 30000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (bag.Count >= count) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Only received {bag.Count} of {count} expected messages within {timeoutMs}ms");
    }
}

public class HotTailMessage
{
    public string Id { get; set; } = string.Empty;
}

public class NodeSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class HotTailHandler
{
    public static void Handle(HotTailMessage message, NodeSink sink)
    {
        sink.Received.Add(message.Id);
    }
}
