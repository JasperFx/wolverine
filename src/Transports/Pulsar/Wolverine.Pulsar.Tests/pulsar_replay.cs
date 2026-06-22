using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3184: bounded one-shot replay via a non-durable Reader cursor that never touches the live
// durable subscription.
public class pulsar_replay
{
    private static Task<IHost> publisherAsync(string topic) => WolverineHost.ForAsync(opts =>
    {
        opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
        opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
    });

    // A host that runs the replay through the normal handler pipeline but does NOT listen to the topic,
    // so its sink only ever sees replayed messages (never the live stream).
    private static Task<IHost> replayerAsync() => WolverineHost.ForAsync(opts =>
    {
        opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
        opts.Services.AddSingleton<ReplaySink>();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<ReplayHandler>();
    });

    private static Task<IHost> liveListenerAsync(string topic, string subscription) => WolverineHost.ForAsync(opts =>
    {
        opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
        opts.ListenToPulsarTopic(topic)
            .SubscriptionName(subscription)
            .ProcessInline()
            .BeginAtEarliest();
        opts.Services.AddSingleton<ReplaySink>();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<ReplayHandler>();
    });

    [Fact]
    public async Task replay_reprocesses_the_whole_topic_without_touching_the_live_subscription()
    {
        var topic = $"persistent://public/default/replay-all-{Guid.NewGuid():N}";
        var subscription = "sub-" + Guid.NewGuid().ToString("N");

        using var publisher = await publisherAsync(topic);
        using var live = await liveListenerAsync(topic, subscription);
        var liveSink = live.Services.GetRequiredService<ReplaySink>();

        for (var i = 0; i < 6; i++)
        {
            await publisher.SendAsync(new ReplayMessage { Id = "m-" + i });
        }

        await waitForCountAsync(liveSink.Received, 6);

        // Replay the entire topic on a separate host that is NOT listening, so its sink only sees the
        // replayed messages.
        using var replayer = await replayerAsync();
        var replaySink = replayer.Services.GetRequiredService<ReplaySink>();

        var result = await replayer.ReplayPulsarTopicAsync(new PulsarReplayRequest { Topic = topic });

        result.MessagesReplayed.ShouldBe(6);
        await waitForCountAsync(replaySink.Received, 6);
        replaySink.Received.OrderBy(x => x).ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4", "m-5"]);

        // The live durable subscription must be untouched: it already saw all six and stays at the tail.
        // Publishing one more delivers exactly that one message (no reset-to-earliest, no duplicates).
        await publisher.SendAsync(new ReplayMessage { Id = "m-6" });
        await waitForCountAsync(liveSink.Received, 7);

        liveSink.Received.OrderBy(x => x)
            .ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4", "m-5", "m-6"]);
    }

    [Fact]
    public async Task replay_from_timestamp_reprocesses_only_messages_after_it()
    {
        var topic = $"persistent://public/default/replay-ts-{Guid.NewGuid():N}";

        using var publisher = await publisherAsync(topic);

        await publisher.SendAsync(new ReplayMessage { Id = "old-0" });
        await publisher.SendAsync(new ReplayMessage { Id = "old-1" });

        await Task.Delay(1500);
        var boundary = DateTimeOffset.UtcNow;
        await Task.Delay(1500);

        await publisher.SendAsync(new ReplayMessage { Id = "new-0" });
        await publisher.SendAsync(new ReplayMessage { Id = "new-1" });
        await publisher.SendAsync(new ReplayMessage { Id = "new-2" });

        using var replayer = await replayerAsync();
        var replaySink = replayer.Services.GetRequiredService<ReplaySink>();

        var result = await replayer.ReplayPulsarTopicAsync(new PulsarReplayRequest
        {
            Topic = topic,
            FromTimestamp = boundary
        });

        result.MessagesReplayed.ShouldBe(3);
        await waitForCountAsync(replaySink.Received, 3);
        replaySink.Received.OrderBy(x => x).ShouldBe(["new-0", "new-1", "new-2"]);
    }

    [Fact]
    public async Task replay_of_empty_topic_returns_zero()
    {
        var topic = $"persistent://public/default/replay-empty-{Guid.NewGuid():N}";

        // Touch the topic so it exists but has no messages.
        using var publisher = await publisherAsync(topic);

        using var replayer = await replayerAsync();
        var result = await replayer.ReplayPulsarTopicAsync(new PulsarReplayRequest { Topic = topic });

        result.MessagesReplayed.ShouldBe(0);
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

public class ReplayMessage
{
    public string Id { get; set; } = string.Empty;
}

public class ReplaySink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class ReplayHandler
{
    public static void Handle(ReplayMessage message, ReplaySink sink)
    {
        sink.Received.Add(message.Id);
    }
}
