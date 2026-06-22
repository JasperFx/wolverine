using System.Buffers;
using System.Text;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3185: producer deduplication. The same envelope re-sent (e.g. an outbox resend) carries the same
// sequence id, so the broker discards the duplicate; a distinct envelope is delivered.
[Collection("pulsar")]
public class pulsar_producer_dedup
{
    // ---- config unit test (no broker) ----

    [Fact]
    public void enable_deduplication_sets_the_flag_and_name()
    {
        var transport = new PulsarTransport();
        var endpoint = transport.EndpointFor("persistent://public/default/dedup-config");
        var config = new PulsarSubscriberConfiguration(endpoint);

        config.EnableDeduplication("orders-producer");
        ((Wolverine.Configuration.IDelayedEndpointConfiguration)config).Apply();

        endpoint.DeduplicationEnabled.ShouldBeTrue();
        endpoint.ProducerName.ShouldBe("orders-producer");
    }

    // ---- end-to-end (Pulsar docker) ----

    [Fact]
    public async Task resending_the_same_envelope_is_deduplicated_by_the_broker()
    {
        var shortName = $"dedup-{Guid.NewGuid():N}";
        var topic = $"persistent://public/default/{shortName}";

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
        });

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<PulsarTransport>();
        var endpoint = transport.EndpointFor(topic);
        endpoint.DeduplicationEnabled = true;
        endpoint.ProducerName = "dedup-test-producer";

        // Enable broker-side deduplication on the namespace before producing.
        await enableNamespaceDeduplicationAsync();

        await using var sender = new PulsarSender(runtime, endpoint, transport, CancellationToken.None);

        var first = BuildEnvelope("a");
        await sender.SendAsync(first);
        await sender.SendAsync(first);   // exact resend of the same envelope -> deduplicated

        var second = BuildEnvelope("b");  // distinct envelope -> delivered
        await sender.SendAsync(second);

        // Only two distinct messages should have actually landed on the topic.
        var delivered = await countMessagesAsync(transport, topic, expected: 2);
        delivered.ShouldBe(2);
    }

    private static Envelope BuildEnvelope(string id)
    {
        return new Envelope(new DedupMessage { Id = id })
        {
            Data = Encoding.UTF8.GetBytes($"{{\"Id\":\"{id}\"}}"),
            MessageType = "dedup-message",
            ContentType = "application/json"
        };
    }

    private static async Task enableNamespaceDeduplicationAsync()
    {
        using var http = new HttpClient();
        var url = $"{PulsarContainerFixture.HttpServiceUrl}/admin/v2/namespaces/public/default/deduplication";
        using var content = new StringContent("true", Encoding.UTF8, "application/json");
        var response = await http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        // Give the broker a moment to apply the policy before producing.
        await Task.Delay(1.Seconds());
    }

    private static async Task<int> countMessagesAsync(PulsarTransport transport, string topic, int expected)
    {
        await using var consumer = transport.Client!.NewConsumer()
            .SubscriptionName("count-" + Guid.NewGuid().ToString("N"))
            .SubscriptionType(SubscriptionType.Exclusive)
            .InitialPosition(SubscriptionInitialPosition.Earliest)
            .Topic(topic)
            .Create();

        var count = 0;
        // Keep trying until an overall deadline so we tolerate cold-start connect latency; only stop early
        // once we've seen at least the expected count and then hit an idle gap (so we'd also catch an
        // over-count if dedup failed to suppress anything).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(3.Seconds());
            IMessage<ReadOnlySequence<byte>> message;
            try
            {
                message = await consumer.Receive(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Idle window with nothing delivered. If we've already got everything expected, we're
                // done; otherwise keep waiting for slow delivery.
                if (count >= expected)
                {
                    break;
                }

                continue;
            }

            count++;
            await consumer.Acknowledge(message, CancellationToken.None);
        }

        return count;
    }
}

public class DedupMessage
{
    public string Id { get; set; } = string.Empty;
}
