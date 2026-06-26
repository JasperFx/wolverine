using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3177: per-message redelivery via RedeliverUnacknowledgedMessages([messageId]) instead of
// redeliver-all / ack+resend. (DotPulsar 5.1.2 has no nack; delayed/backoff redelivery is #3182.)
public class per_message_redelivery
{
    [Fact]
    public void use_native_redelivery_sets_the_flag()
    {
        var transport = new PulsarTransport();
        var endpoint = transport[new Uri("pulsar://persistent/public/default/redeliver")];
        var config = new PulsarListenerConfiguration(endpoint);

        endpoint.UseNativeRedelivery.ShouldBeFalse();

        config.UseNativeRedelivery();
        ((IDelayedEndpointConfiguration)config).Apply();

        endpoint.UseNativeRedelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task native_redelivery_redelivers_a_failed_message_until_it_succeeds()
    {
        var topic = $"persistent://public/default/redeliver-{Guid.NewGuid():N}";

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<RedeliveryMessage>().ToPulsarTopic(topic).SendInline();
            // With UseNativeRedelivery, a failure with no retry-letter/DLQ configured leaves the
            // message unacknowledged and asks Pulsar to redeliver just it.
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .UseNativeRedelivery();

            opts.Services.AddSingleton<RedeliverySink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<RedeliveryHandler>();
        });

        await host.SendAsync(new RedeliveryMessage { Id = "m1" });

        var sink = host.Services.GetRequiredService<RedeliverySink>();
        await waitForConditionAsync(() => sink.Succeeded.Contains("m1"), 30000);

        // First physical delivery threw; Pulsar redelivered the same message and the retry succeeded.
        sink.Deliveries.TryGetValue("m1", out var deliveries).ShouldBeTrue();
        deliveries.ShouldBeGreaterThanOrEqualTo(2);
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

public class RedeliveryMessage
{
    public string Id { get; set; } = string.Empty;
}

public class RedeliverySink
{
    public ConcurrentDictionary<string, int> Deliveries { get; } = new();
    public ConcurrentBag<string> Succeeded { get; } = new();
}

public class RedeliveryHandler
{
    public static void Handle(RedeliveryMessage message, RedeliverySink sink)
    {
        var count = sink.Deliveries.AddOrUpdate(message.Id, 1, (_, c) => c + 1);

        // Fail the very first physical delivery so Pulsar has to redeliver it.
        if (count == 1)
        {
            throw new InvalidOperationException("Simulated failure on first delivery");
        }

        sink.Succeeded.Add(message.Id);
    }
}
