using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3184: ephemeral hot-tail / broadcast consume built on a non-durable Reader at the tail.
public class pulsar_hot_tail
{
    private static PulsarEndpoint applyListener(Action<PulsarListenerConfiguration> configure)
    {
        var transport = new PulsarTransport();
        var endpoint = new PulsarEndpoint("pulsar://persistent/public/default/events".ToUri(), transport);
        var config = new PulsarListenerConfiguration(endpoint);
        configure(config);
        ((IDelayedEndpointConfiguration)config).Apply();
        return endpoint;
    }

    // ---- config unit test (no broker) ----

    [Fact]
    public void tail_from_latest_marks_hot_tail_and_latest()
    {
        var endpoint = applyListener(c => c.TailFromLatest());
        endpoint.IsHotTail.ShouldBeTrue();
        endpoint.SubscriptionInitialPosition.ShouldBe(DotPulsar.SubscriptionInitialPosition.Latest);
    }

    // ---- end-to-end (Pulsar docker) ----

    private static Task<IHost> publisherAsync(string topic) => WolverineHost.ForAsync(opts =>
    {
        opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
        opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
    });

    private static Task<IHost> hotTailNodeAsync(string topic) => WolverineHost.ForAsync(opts =>
    {
        opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
        opts.ListenToPulsarTopic(topic).ProcessInline().TailFromLatest();
        opts.Services.AddSingleton<HotTailSink>();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<HotTailHandler>();
    });

    [Fact]
    public async Task hot_tail_delivers_all_tail_messages_to_every_node_and_never_replays_history()
    {
        var topic = $"persistent://public/default/hottail-{Guid.NewGuid():N}";

        using var publisher = await publisherAsync(topic);

        // Published BEFORE any hot-tail reader exists -> must NOT be delivered (no history replay).
        for (var i = 0; i < 3; i++)
        {
            await publisher.SendAsync(new HotTailMessage { Id = "pre-" + i });
        }

        using var nodeA = await hotTailNodeAsync(topic);
        using var nodeB = await hotTailNodeAsync(topic);

        // Latest means only messages published AFTER the readers attach are seen — give them a moment.
        await Task.Delay(3.Seconds());

        for (var i = 0; i < 5; i++)
        {
            await publisher.SendAsync(new HotTailMessage { Id = "m-" + i });
        }

        var sinkA = nodeA.Services.GetRequiredService<HotTailSink>();
        var sinkB = nodeB.Services.GetRequiredService<HotTailSink>();
        await waitForCountAsync(sinkA.Received, 5);
        await waitForCountAsync(sinkB.Received, 5);

        // Both nodes received all five tail messages and none of the pre-subscription history.
        sinkA.Received.OrderBy(x => x).ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4"]);
        sinkB.Received.OrderBy(x => x).ShouldBe(["m-0", "m-1", "m-2", "m-3", "m-4"]);
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

public class HotTailSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class HotTailHandler
{
    public static void Handle(HotTailMessage message, HotTailSink sink)
    {
        sink.Received.Add(message.Id);
    }
}
