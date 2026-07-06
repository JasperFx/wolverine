using DotPulsar.Extensions;
using Shouldly;
using Wolverine.Pulsar.ErrorHandling;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// Registration-level coverage for named Pulsar brokers (GH-3308) that needs no running broker, so it always
/// runs in CI. A named broker is a second, independent <see cref="PulsarTransport"/> whose <c>Protocol</c>
/// (and therefore its endpoints' URI scheme) is the broker name, so its endpoints never collide with the
/// default <c>pulsar://</c> broker.
/// </summary>
public class PulsarNamedBrokerTests
{
    private readonly BrokerName theName = new("secondary");

    [Fact]
    public void adds_a_distinct_transport_instance_per_broker()
    {
        var options = new WolverineOptions();
        options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://localhost:6650")));
        options.AddNamedPulsarBroker(theName, b => b.ServiceUrl(new Uri("pulsar://localhost:6660")));

        var transports = options.Transports.OfType<PulsarTransport>().ToList();
        transports.Count.ShouldBe(2);
        transports.Select(x => x.Protocol).OrderBy(x => x).ShouldBe(["pulsar", "secondary"]);
    }

    [Fact]
    public void named_broker_endpoints_use_the_broker_name_as_their_uri_scheme()
    {
        var options = new WolverineOptions();
        options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://localhost:6650")));
        options.AddNamedPulsarBroker(theName, b => b.ServiceUrl(new Uri("pulsar://localhost:6660")));

        var named = options.Transports.OfType<PulsarTransport>().Single(x => x.Protocol == "secondary");
        named.EndpointFor("persistent://public/default/orders").Uri
            .ShouldBe(new Uri("secondary://persistent/public/default/orders"));

        // ...and the default broker keeps the canonical pulsar:// scheme.
        var @default = options.Transports.OfType<PulsarTransport>().Single(x => x.Protocol == "pulsar");
        @default.EndpointFor("persistent://public/default/orders").Uri
            .ShouldBe(new Uri("pulsar://persistent/public/default/orders"));
    }

    [Fact]
    public void get_or_create_by_name_returns_the_named_instance()
    {
        var options = new WolverineOptions();
        options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://localhost:6650")));
        options.AddNamedPulsarBroker(theName, b => b.ServiceUrl(new Uri("pulsar://localhost:6660")));

        var named = options.Transports.GetOrCreate<PulsarTransport>(theName);
        named.Protocol.ShouldBe("secondary");

        var @default = options.Transports.GetOrCreate<PulsarTransport>();
        @default.Protocol.ShouldBe("pulsar");

        named.ShouldNotBeSameAs(@default);
    }

    [Fact]
    public void listen_and_publish_on_named_broker_target_the_named_transport()
    {
        var options = new WolverineOptions();
        options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://localhost:6650")));
        options.AddNamedPulsarBroker(theName, b => b.ServiceUrl(new Uri("pulsar://localhost:6660")));

        options.ListenToPulsarTopicOnNamedBroker(theName, "persistent://public/default/incoming");

        var named = options.Transports.OfType<PulsarTransport>().Single(x => x.Protocol == "secondary");
        var endpoint = named.EndpointFor("persistent://public/default/incoming");
        endpoint.IsListener.ShouldBeTrue();
        endpoint.Uri.Scheme.ShouldBe("secondary");
    }
}
