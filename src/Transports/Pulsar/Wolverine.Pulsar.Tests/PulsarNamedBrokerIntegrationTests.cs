using DotPulsar.Extensions;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// CI-safe end-to-end coverage for named Pulsar brokers (GH-3308). The default broker and the named broker
/// both point at the single <see cref="PulsarContainerFixture.ServiceUrl"/> container, but the named broker
/// exercises a distinct topic and its own <c>secondary://</c> URI scheme. This proves the wiring — a message
/// published and consumed on the named broker round-trips and arrives stamped with the named broker's URI
/// scheme (the receive pipeline sets <c>Destination</c> from the listener endpoint's URI). Proving true
/// cluster isolation (a second container) is a separate, local-only test — see
/// <see cref="PulsarPerTenantConnectionTests"/>.
/// </summary>
[Collection("pulsar")]
public class PulsarNamedBrokerIntegrationTests
{
    private readonly BrokerName theName = new("secondary");

    [Fact]
    public async Task round_trips_a_message_over_the_named_broker()
    {
        var topic = $"persistent://public/default/named-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedBrokerInbound";
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.AddNamedPulsarBroker(theName, b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

                opts.PublishMessage<NamedBrokerMessage>()
                    .ToPulsarTopicOnNamedBroker(theName, topic).SendInline();
                opts.ListenToPulsarTopicOnNamedBroker(theName, topic)
                    .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                    .BeginAtEarliest();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NamedBrokerHandler>();
            })
            .StartAsync();

        var session = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<NamedBrokerMessage>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedBrokerMessage("round-trip")));

        var received = session.Received.SingleEnvelope<NamedBrokerMessage>();
        received.Message.ShouldBeOfType<NamedBrokerMessage>().Id.ShouldBe("round-trip");

        // The receive pipeline stamps Destination from the listener endpoint's URI, so the consumed envelope
        // carries the named broker's "secondary" scheme rather than the default "pulsar".
        received.Destination!.Scheme.ShouldBe("secondary");
    }
}

public record NamedBrokerMessage(string Id);

public class NamedBrokerHandler
{
    public void Handle(NamedBrokerMessage message)
    {
        // no-op; the tracking session observes receipt
    }
}
