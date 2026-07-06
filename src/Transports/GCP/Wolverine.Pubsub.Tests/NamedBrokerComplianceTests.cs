using Google.Api.Gax;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class NamedBrokerComplianceTests
{
    // A named broker is a second, independent PubsubTransport whose Protocol (and therefore URI scheme) is the
    // broker name, and which carries its own project id. No broker needed.
    [Fact]
    public void named_broker_is_a_distinct_transport_with_its_own_protocol_and_project()
    {
        var options = new WolverineOptions();

        options.UsePubsub("wolverine");
        options.AddNamedPubsubBroker(new BrokerName("americas"), "wolverine2");

        var defaultTransport = options.Transports.GetOrCreate<PubsubTransport>();
        var named = options.Transports.OfType<PubsubTransport>().Single(x => x.Protocol == "americas");

        named.ShouldNotBeSameAs(defaultTransport);
        defaultTransport.Protocol.ShouldBe(PubsubTransport.ProtocolName);
        defaultTransport.ProjectId.ShouldBe("wolverine");
        named.Protocol.ShouldBe("americas");
        named.ProjectId.ShouldBe("wolverine2");
    }

    [Fact]
    public void named_broker_endpoint_uri_scheme_is_the_broker_name()
    {
        var options = new WolverineOptions();

        options.AddNamedPubsubBroker(new BrokerName("americas"), "wolverine2");
        options.ListenToPubsubTopicOnNamedBroker(new BrokerName("americas"), "colors");

        var named = options.Transports.OfType<PubsubTransport>().Single(x => x.Protocol == "americas");
        var endpoint = named.Topics["colors"];

        endpoint.Uri.Scheme.ShouldBe("americas");
        endpoint.Uri.ShouldBe(new Uri("americas://wolverine2/colors"));
        named.ResourceUri.ShouldBe(new Uri("americas://wolverine2"));
    }
}

// Integration: round-trip a message over a second project on the same emulator, addressed exclusively through the
// named broker. Skip-guarded when the emulator/Docker is unavailable.
public class NamedBrokerRoundTripTests : IAsyncLifetime
{
    private IHost? _host;
    private bool _skip;

    public async Task InitializeAsync()
    {
        _skip = !await TestingExtensions.IsEmulatorAvailable();
        if (_skip)
        {
            return;
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Default/shared broker on project "wolverine"...
                opts.UsePubsubTesting().AutoProvision().AutoPurgeOnStartup();

                // ...plus a named broker on a second project "wolverine2" on the same emulator.
                opts.AddNamedPubsubBrokerTesting(new BrokerName("americas"), "wolverine2").AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<NamedBrokerMessage>()
                    .ToPubsubTopicOnNamedBroker(new BrokerName("americas"), "named-broker-colors");
                opts.ListenToPubsubTopicOnNamedBroker(new BrokerName("americas"), "named-broker-colors");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task round_trips_a_message_through_the_named_broker()
    {
        if (_skip)
        {
            return;
        }

        var message = new NamedBrokerMessage("blue");

        var session = await _host!.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .SendMessageAndWaitAsync(message);

        var received = session.Received.SingleEnvelope<NamedBrokerMessage>();
        received.Message.ShouldBeOfType<NamedBrokerMessage>().Color.ShouldBe("blue");

        // The receiving endpoint lives on the named broker, so its Uri scheme is the broker name.
        received.Destination!.Scheme.ShouldBe("americas");
    }
}

public record NamedBrokerMessage(string Color);

public static class NamedBrokerMessageHandler
{
    public static void Handle(NamedBrokerMessage message)
    {
    }
}
