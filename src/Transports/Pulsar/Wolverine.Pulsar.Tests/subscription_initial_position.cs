using System.Collections.Concurrent;
using DotPulsar;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3178: subscription initial position (Earliest/Latest) — the Pulsar analogue of Kafka's
// BeginAtEarliest/BeginAtLatest cold-start control (#3146).
public class subscription_initial_position
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

    // ---- config unit tests (no broker) ----

    [Fact]
    public void default_initial_position_is_latest()
    {
        var endpoint = new PulsarEndpoint("pulsar://persistent/public/default/events".ToUri(), new PulsarTransport());
        endpoint.SubscriptionInitialPosition.ShouldBe(SubscriptionInitialPosition.Latest);
    }

    [Fact]
    public void begin_at_earliest_sets_initial_position()
    {
        var endpoint = applyListener(c => c.BeginAtEarliest());
        endpoint.SubscriptionInitialPosition.ShouldBe(SubscriptionInitialPosition.Earliest);
    }

    [Fact]
    public void begin_at_latest_sets_initial_position()
    {
        var endpoint = applyListener(c => c.BeginAtLatest());
        endpoint.SubscriptionInitialPosition.ShouldBe(SubscriptionInitialPosition.Latest);
    }

    [Fact]
    public void subscription_initial_position_sets_value()
    {
        var endpoint = applyListener(c => c.SubscriptionInitialPosition(SubscriptionInitialPosition.Earliest));
        endpoint.SubscriptionInitialPosition.ShouldBe(SubscriptionInitialPosition.Earliest);
    }

    // ---- end-to-end (Pulsar docker) ----

    [Fact]
    public async Task earliest_replays_messages_published_before_the_subscription_existed()
    {
        var topic = $"persistent://public/default/initpos-early-{Guid.NewGuid():N}";
        var subscription = "sub-" + Guid.NewGuid().ToString("N");

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
        });

        // Publish BEFORE any consumer/subscription exists for this topic.
        for (var i = 0; i < 4; i++)
        {
            await publisher.SendAsync(new InitialPositionMessage { Id = "pre-" + i });
        }

        using var consumer = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName(subscription)
                .ProcessInline()
                .BeginAtEarliest();
            opts.Services.AddSingleton<InitialPositionSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<InitialPositionHandler>();
        });

        var sink = consumer.Services.GetRequiredService<InitialPositionSink>();
        await waitForCountAsync(sink.Received, 4);
        sink.Received.OrderBy(x => x).ShouldBe(["pre-0", "pre-1", "pre-2", "pre-3"]);
    }

    [Fact]
    public async Task latest_consumes_only_messages_published_after_the_subscription_exists()
    {
        var topic = $"persistent://public/default/initpos-late-{Guid.NewGuid():N}";
        var subscription = "sub-" + Guid.NewGuid().ToString("N");

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
        });

        // Published before the Latest subscription exists -> must NOT be delivered.
        for (var i = 0; i < 3; i++)
        {
            await publisher.SendAsync(new InitialPositionMessage { Id = "pre-" + i });
        }

        using var consumer = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName(subscription)
                .ProcessInline()
                .BeginAtLatest();
            opts.Services.AddSingleton<InitialPositionSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<InitialPositionHandler>();
        });

        // Give the subscription time to be established before publishing the "post" messages.
        await Task.Delay(3.Seconds());

        for (var i = 0; i < 3; i++)
        {
            await publisher.SendAsync(new InitialPositionMessage { Id = "post-" + i });
        }

        var sink = consumer.Services.GetRequiredService<InitialPositionSink>();
        await waitForCountAsync(sink.Received, 3);

        // Only the post-subscription messages, never the pre-subscription ones.
        sink.Received.OrderBy(x => x).ShouldBe(["post-0", "post-1", "post-2"]);
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

public class InitialPositionMessage
{
    public string Id { get; set; } = string.Empty;
}

public class InitialPositionSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class InitialPositionHandler
{
    public static void Handle(InitialPositionMessage message, InitialPositionSink sink)
    {
        sink.Received.Add(message.Id);
    }
}
