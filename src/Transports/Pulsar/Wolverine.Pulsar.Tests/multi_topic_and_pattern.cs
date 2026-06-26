using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DotPulsar;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3181: multi-topic & regex/pattern subscriptions — one Pulsar consumer over many topics
// (analogue of Kafka topic groups) and over a regex pattern.
public class multi_topic_and_pattern
{
    private static PulsarEndpoint applyListener(string primary, Action<PulsarListenerConfiguration> configure)
    {
        var transport = new PulsarTransport();
        var endpoint = transport[PulsarEndpointUri.Topic(primary)];
        var config = new PulsarListenerConfiguration(endpoint);
        configure(config);
        ((IDelayedEndpointConfiguration)config).Apply();
        return endpoint;
    }

    // ---- config unit tests (no broker) ----

    [Fact]
    public void topics_adds_additional_topics_and_dedupes_primary()
    {
        var endpoint = applyListener("persistent://public/default/one",
            c => c.Topics("persistent://public/default/two", "persistent://public/default/three",
                "persistent://public/default/one"));

        endpoint.AllTopicPaths().ShouldBe([
            "persistent://public/default/one",
            "persistent://public/default/two",
            "persistent://public/default/three"
        ]);
    }

    [Fact]
    public void topics_pattern_sets_pattern_and_mode()
    {
        var pattern = new Regex("persistent://public/default/orders-.*");
        var endpoint = applyListener("persistent://public/default/orders-all",
            c => c.TopicsPattern(pattern, RegexSubscriptionMode.All));

        endpoint.TopicsPattern.ShouldBe(pattern);
        endpoint.RegexSubscriptionMode.ShouldBe(RegexSubscriptionMode.All);
    }

    [Fact]
    public void topics_validates_malformed_paths_eagerly()
    {
        Should.Throw<Exception>(() => applyListener("persistent://public/default/one",
            c => c.Topics("not-a-valid-topic")));
    }

    // ---- end-to-end (Pulsar docker) ----

    [Fact]
    public async Task single_listener_consumes_multiple_explicit_topics()
    {
        var prefix = "mt-" + Guid.NewGuid().ToString("N");
        var topic1 = $"persistent://public/default/{prefix}-1";
        var topic2 = $"persistent://public/default/{prefix}-2";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<MultiTopicMessage>().ToPulsarTopic(topic1).SendInline();
            opts.PublishMessage<OtherTopicMessage>().ToPulsarTopic(topic2).SendInline();
        });

        await publisher.SendAsync(new MultiTopicMessage { Id = "from-1" });
        await publisher.SendAsync(new OtherTopicMessage { Id = "from-2" });

        using var consumer = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic1)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .Topics(topic2)
                .ProcessInline()
                .BeginAtEarliest();
            opts.Services.AddSingleton<MultiTopicSink>();
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<MultiTopicHandler>()
                .IncludeType<OtherTopicHandler>();
        });

        var sink = consumer.Services.GetRequiredService<MultiTopicSink>();
        await waitForCountAsync(sink.Received, 2);
        sink.Received.OrderBy(x => x).ShouldBe(["from-1", "from-2"]);
    }

    [Fact]
    public async Task single_listener_consumes_a_regex_pattern()
    {
        var prefix = "ptn" + Guid.NewGuid().ToString("N");
        var topicA = $"persistent://public/default/{prefix}-a";
        var topicB = $"persistent://public/default/{prefix}-b";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<MultiTopicMessage>().ToPulsarTopic(topicA).SendInline();
            opts.PublishMessage<OtherTopicMessage>().ToPulsarTopic(topicB).SendInline();
        });

        // Create the topics (and their backlog) before the pattern subscription matches them.
        await publisher.SendAsync(new MultiTopicMessage { Id = "ptn-a" });
        await publisher.SendAsync(new OtherTopicMessage { Id = "ptn-b" });

        using var consumer = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic($"persistent://public/default/{prefix}-a")
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .TopicsPattern(new Regex($"persistent://public/default/{prefix}-.*"))
                .ProcessInline()
                .BeginAtEarliest();
            opts.Services.AddSingleton<MultiTopicSink>();
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<MultiTopicHandler>()
                .IncludeType<OtherTopicHandler>();
        });

        var sink = consumer.Services.GetRequiredService<MultiTopicSink>();
        await waitForCountAsync(sink.Received, 2, 45000);
        sink.Received.OrderBy(x => x).ShouldBe(["ptn-a", "ptn-b"]);
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

public class MultiTopicMessage
{
    public string Id { get; set; } = string.Empty;
}

public class OtherTopicMessage
{
    public string Id { get; set; } = string.Empty;
}

public class MultiTopicSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class MultiTopicHandler
{
    public static void Handle(MultiTopicMessage message, MultiTopicSink sink) => sink.Received.Add(message.Id);
}

public class OtherTopicHandler
{
    public static void Handle(OtherTopicMessage message, MultiTopicSink sink) => sink.Received.Add(message.Id);
}
