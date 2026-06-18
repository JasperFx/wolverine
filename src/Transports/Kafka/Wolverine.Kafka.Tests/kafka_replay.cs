using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;

namespace Wolverine.Kafka.Tests;

// GH-3147: bounded one-shot replay. Reuses HotTailMessage / NodeSink / HotTailHandler.
public class kafka_replay
{
    private Task<IHost> hostAsync(string topic, string liveGroup) => WolverineHost.ForAsync(opts =>
    {
        opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.PublishAllMessages().ToKafkaTopic(topic).SendInline();
        opts.ListenToKafkaTopic(topic)
            .ProcessInline()
            .ConfigureConsumer(x =>
            {
                x.GroupId = liveGroup;
                x.AutoOffsetReset = AutoOffsetReset.Earliest;
            });
        opts.Services.AddSingleton<NodeSink>();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<HotTailHandler>();
    });

    private static long QueryCommittedOffset(string groupId, string topic)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = groupId
        };
        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        var committed = consumer.Committed([new TopicPartition(topic, new Partition(0))], TimeSpan.FromSeconds(10));
        return committed[0].Offset.Value;
    }

    [Fact]
    public async Task replay_from_offset_reprocesses_the_window_without_touching_the_live_group()
    {
        var topic = Guid.NewGuid().ToString();
        var liveGroup = Guid.NewGuid().ToString();

        using var host = await hostAsync(topic, liveGroup);
        var sink = host.Services.GetRequiredService<NodeSink>();

        // Single partition (auto-provisioned default), so offsets 0..5 == m-0..m-5.
        for (var i = 0; i < 6; i++)
        {
            await host.SendAsync(new HotTailMessage { Id = "m-" + i });
        }

        await waitForCountAsync(sink.Received, 6);
        // Let the live group commit its progress through the topic.
        await Task.Delay(2000);
        var committedBefore = QueryCommittedOffset(liveGroup, topic);

        // Now replay just the tail of the window through the pipeline again.
        sink.Received.Clear();
        var result = await host.ReplayKafkaTopicAsync(new KafkaReplayRequest { Topic = topic, FromOffset = 2 });

        result.RecordsReplayed.ShouldBe(4);
        await waitForCountAsync(sink.Received, 4);
        sink.Received.OrderBy(x => x).ShouldBe(["m-2", "m-3", "m-4", "m-5"]);

        // The live group's committed offset must be untouched by the replay (it used a throwaway group).
        QueryCommittedOffset(liveGroup, topic).ShouldBe(committedBefore);
    }

    [Fact]
    public async Task replay_from_timestamp_reprocesses_only_records_after_it()
    {
        var topic = Guid.NewGuid().ToString();
        var liveGroup = Guid.NewGuid().ToString();

        using var host = await hostAsync(topic, liveGroup);
        var sink = host.Services.GetRequiredService<NodeSink>();

        await host.SendAsync(new HotTailMessage { Id = "old-0" });
        await host.SendAsync(new HotTailMessage { Id = "old-1" });
        await waitForCountAsync(sink.Received, 2);

        await Task.Delay(1500);
        var boundary = DateTimeOffset.UtcNow;
        await Task.Delay(1500);

        await host.SendAsync(new HotTailMessage { Id = "new-0" });
        await host.SendAsync(new HotTailMessage { Id = "new-1" });
        await host.SendAsync(new HotTailMessage { Id = "new-2" });
        await waitForCountAsync(sink.Received, 5);

        sink.Received.Clear();
        var result = await host.ReplayKafkaTopicAsync(new KafkaReplayRequest { Topic = topic, FromTimestamp = boundary });

        result.RecordsReplayed.ShouldBe(3);
        await waitForCountAsync(sink.Received, 3);
        sink.Received.OrderBy(x => x).ShouldBe(["new-0", "new-1", "new-2"]);
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
