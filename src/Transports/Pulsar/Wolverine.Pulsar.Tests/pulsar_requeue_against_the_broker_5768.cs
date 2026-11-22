using System.Collections.Concurrent;
using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-5768, against a real broker: what the broker does with a requeued copy
[Collection("pulsar")]
public class pulsar_requeue_against_the_broker_5768
{
    [Fact]
    public async Task a_message_requeued_twice_under_deduplication_is_not_dropped_by_the_broker()
    {
        await enableNamespaceDeduplicationAsync();

        var topic = $"persistent://public/default/requeue-dedup-{Guid.NewGuid():N}";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<RequeuedTwiceMessage>().ToPulsarTopic(topic).SendInline();
        });

        using var consumer = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .ProcessInline()
                .BeginAtEarliest();

            // The listener's requeue producer shares the endpoint's deduplication setting
            var endpoint = opts.Transports.GetOrCreate<PulsarTransport>().EndpointFor(topic);
            endpoint.DeduplicationEnabled = true;
            endpoint.ProducerName = "requeue-" + Guid.NewGuid().ToString("N");

            opts.OnException<RequeueTestFailure>().Requeue(5);

            opts.Services.AddSingleton<RequeueSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<RequeuedTwiceMessageHandler>();
        });

        await publisher.SendAsync(new RequeuedTwiceMessage { Id = "m1" });

        // Two failures means two requeued copies of the same envelope. The second one used to carry the first
        // one's sequence id, and the broker dropped it as a duplicate while the original was already acked
        var sink = consumer.Services.GetRequiredService<RequeueSink>();
        await waitForAsync(() => sink.Succeeded.Contains("m1"), sink);

        sink.Deliveries["m1"].ShouldBe(3);
    }

    [Fact]
    public async Task a_buffered_listener_with_native_redelivery_does_not_lose_a_failed_message()
    {
        var topic = $"persistent://public/default/requeue-buffered-{Guid.NewGuid():N}";

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<FailsOnceMessage>().ToPulsarTopic(topic).SendInline();

            // A buffered listener acks every message as it reaches the in-memory queue, so asking Pulsar to
            // redeliver the failed one afterwards did nothing at all
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .BufferedInMemory()
                .UseNativeRedelivery();

            opts.Services.AddSingleton<RequeueSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<FailsOnceMessageHandler>();
        });

        await host.SendAsync(new FailsOnceMessage { Id = "b1" });

        var sink = host.Services.GetRequiredService<RequeueSink>();
        await waitForAsync(() => sink.Succeeded.Contains("b1"), sink);

        sink.Deliveries["b1"].ShouldBeGreaterThanOrEqualTo(2);
    }

    private static async Task waitForAsync(Func<bool> condition, RequeueSink sink)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"The message was never handled successfully. Deliveries: {string.Join(", ", sink.Deliveries.Select(x => $"{x.Key}={x.Value}"))}");
    }

    private static async Task enableNamespaceDeduplicationAsync()
    {
        using var http = new HttpClient();
        var url = $"{PulsarContainerFixture.HttpServiceUrl}/admin/v2/namespaces/public/default/deduplication";
        using var content = new StringContent("true", Encoding.UTF8, "application/json");
        var response = await http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        await Task.Delay(1.Seconds());
    }
}

public class RequeueTestFailure : Exception;

public class RequeueSink
{
    public ConcurrentDictionary<string, int> Deliveries { get; } = new();
    public ConcurrentBag<string> Succeeded { get; } = new();
}

public class RequeuedTwiceMessage
{
    public string Id { get; set; } = string.Empty;
}

public class RequeuedTwiceMessageHandler
{
    public static void Handle(RequeuedTwiceMessage message, RequeueSink sink)
    {
        if (sink.Deliveries.AddOrUpdate(message.Id, 1, (_, c) => c + 1) <= 2)
        {
            throw new RequeueTestFailure();
        }

        sink.Succeeded.Add(message.Id);
    }
}

public class FailsOnceMessage
{
    public string Id { get; set; } = string.Empty;
}

public class FailsOnceMessageHandler
{
    public static void Handle(FailsOnceMessage message, RequeueSink sink)
    {
        if (sink.Deliveries.AddOrUpdate(message.Id, 1, (_, c) => c + 1) == 1)
        {
            throw new RequeueTestFailure();
        }

        sink.Succeeded.Add(message.Id);
    }
}
