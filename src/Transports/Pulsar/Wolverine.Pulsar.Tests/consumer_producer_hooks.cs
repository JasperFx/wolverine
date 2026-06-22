using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3179: per-consumer / per-producer customization hooks against DotPulsar's builders.
public class consumer_producer_hooks
{
    [Fact]
    public void configure_consumer_sets_the_hook()
    {
        var transport = new PulsarTransport();
        var endpoint = transport[new Uri("pulsar://persistent/public/default/hooks")];
        var config = new PulsarListenerConfiguration(endpoint);

        config.ConfigureConsumer(_ => { });
        ((IDelayedEndpointConfiguration)config).Apply();

        endpoint.ConfigureConsumer.ShouldNotBeNull();
    }

    [Fact]
    public void configure_producer_sets_the_hook()
    {
        var transport = new PulsarTransport();
        var endpoint = transport[new Uri("pulsar://persistent/public/default/hooks2")];
        var config = new PulsarSubscriberConfiguration(endpoint);

        config.ConfigureProducer(_ => { });
        ((IDelayedEndpointConfiguration)config).Apply();

        endpoint.ConfigureProducer.ShouldNotBeNull();
    }

    [Fact]
    public async Task per_endpoint_consumer_and_producer_hooks_are_invoked()
    {
        var topic = $"persistent://public/default/hooks-{Guid.NewGuid():N}";
        var consumerConfigured = false;
        var producerConfigured = false;

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

            opts.PublishMessage<HookMessage>().ToPulsarTopic(topic).SendInline()
                .ConfigureProducer(p =>
                {
                    producerConfigured = true;
                    p.ProducerName("wolverine-test-producer-" + Guid.NewGuid().ToString("N"));
                });

            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .ConfigureConsumer(c =>
                {
                    consumerConfigured = true;
                    c.ConsumerName("wolverine-test-consumer-" + Guid.NewGuid().ToString("N"));
                });

            opts.Services.AddSingleton<HookSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<HookHandler>();
        });

        await host.SendAsync(new HookMessage { Id = "h1" });

        var sink = host.Services.GetRequiredService<HookSink>();
        await waitForConditionAsync(() => sink.Received.Contains("h1"), 30000);

        // Both customization callbacks ran while building the real consumer/producer, and the
        // customized consumer/producer still deliver the message end to end.
        consumerConfigured.ShouldBeTrue();
        producerConfigured.ShouldBeTrue();
    }

    private static async Task waitForConditionAsync(Func<bool> condition, int timeoutMs)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition not met within {timeoutMs}ms");
    }
}

public class HookMessage
{
    public string Id { get; set; } = string.Empty;
}

public class HookSink
{
    public ConcurrentBag<string> Received { get; } = new();
}

public class HookHandler
{
    public static void Handle(HookMessage message, HookSink sink) => sink.Received.Add(message.Id);
}
