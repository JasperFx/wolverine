using NATS.Client.JetStream.Models;
using Shouldly;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Coverage for the JetStream consumer <c>DeliverPolicy</c> override surface
/// (transport-wide via <see cref="JetStreamDefaults.DeliverPolicy"/>,
/// per-listener via <see cref="NatsListenerConfiguration.DeliverFrom"/>) and
/// the precedence rule that resolves the two through
/// <see cref="NatsEndpoint.EffectiveDeliverPolicy"/>.
/// </summary>
public class NatsDeliverPolicyTests
{
    private NatsEndpoint EndpointFor(string subject = "orders.created")
    {
        var transport = new NatsTransport();
        return (NatsEndpoint)transport.GetOrCreateEndpoint(NatsEndpointUri.Subject(subject));
    }

    [Fact]
    public void endpoint_default_deliver_policy_is_null()
    {
        // Null means "leave the ConsumerConfig alone and accept the NATS
        // server default of DeliverPolicy.All". Source-compatible with the
        // pre-PR behaviour for hosts that don't opt in.
        EndpointFor().DeliverPolicy.ShouldBeNull();
    }

    [Fact]
    public void transport_default_deliver_policy_is_null()
    {
        new JetStreamDefaults().DeliverPolicy.ShouldBeNull();
    }

    [Fact]
    public void effective_deliver_policy_is_null_when_neither_endpoint_nor_transport_set_it()
    {
        EndpointFor().EffectiveDeliverPolicy.ShouldBeNull();
    }

    [Fact]
    public void effective_deliver_policy_falls_back_to_transport_when_endpoint_is_null()
    {
        var transport = new NatsTransport();
        transport.Configuration.JetStreamDefaults.DeliverPolicy = ConsumerConfigDeliverPolicy.New;

        var endpoint = (NatsEndpoint)transport.GetOrCreateEndpoint(NatsEndpointUri.Subject("topic"));

        endpoint.DeliverPolicy.ShouldBeNull();
        endpoint.EffectiveDeliverPolicy.ShouldBe(ConsumerConfigDeliverPolicy.New);
    }

    [Fact]
    public void endpoint_override_wins_over_transport_default()
    {
        var transport = new NatsTransport();
        transport.Configuration.JetStreamDefaults.DeliverPolicy = ConsumerConfigDeliverPolicy.New;

        var endpoint = (NatsEndpoint)transport.GetOrCreateEndpoint(NatsEndpointUri.Subject("topic"));
        endpoint.DeliverPolicy = ConsumerConfigDeliverPolicy.Last;

        endpoint.EffectiveDeliverPolicy.ShouldBe(ConsumerConfigDeliverPolicy.Last);
    }

    [Fact]
    public void listener_configuration_deliver_from_sets_endpoint_override()
    {
        var endpoint = EndpointFor();
        var configuration = new NatsListenerConfiguration(endpoint);

        configuration.DeliverFrom(ConsumerConfigDeliverPolicy.New);

        // Configuration callbacks are buffered in the IDelayedEndpointConfiguration
        // base — applying them here mirrors what the runtime does at endpoint
        // compile time before BuildListenerAsync runs.
        ((Wolverine.Configuration.IDelayedEndpointConfiguration)configuration).Apply();

        endpoint.DeliverPolicy.ShouldBe(ConsumerConfigDeliverPolicy.New);
        endpoint.EffectiveDeliverPolicy.ShouldBe(ConsumerConfigDeliverPolicy.New);
    }

    [Fact]
    public void listener_configuration_deliver_from_overrides_transport_default()
    {
        var transport = new NatsTransport();
        transport.Configuration.JetStreamDefaults.DeliverPolicy = ConsumerConfigDeliverPolicy.All;

        var endpoint = (NatsEndpoint)transport.GetOrCreateEndpoint(NatsEndpointUri.Subject("topic"));
        var configuration = new NatsListenerConfiguration(endpoint);

        configuration.DeliverFrom(ConsumerConfigDeliverPolicy.New);
        ((Wolverine.Configuration.IDelayedEndpointConfiguration)configuration).Apply();

        endpoint.EffectiveDeliverPolicy.ShouldBe(ConsumerConfigDeliverPolicy.New);
    }

    [Theory]
    [InlineData(ConsumerConfigDeliverPolicy.All)]
    [InlineData(ConsumerConfigDeliverPolicy.Last)]
    [InlineData(ConsumerConfigDeliverPolicy.New)]
    [InlineData(ConsumerConfigDeliverPolicy.LastPerSubject)]
    public void deliver_from_round_trips_through_endpoint(ConsumerConfigDeliverPolicy policy)
    {
        var endpoint = EndpointFor();
        var configuration = new NatsListenerConfiguration(endpoint);

        configuration.DeliverFrom(policy);
        ((Wolverine.Configuration.IDelayedEndpointConfiguration)configuration).Apply();

        endpoint.EffectiveDeliverPolicy.ShouldBe(policy);
    }
}
