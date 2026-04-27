using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

// Locks down GH-2601 for NATS. Unlike other transports, NatsEndpoint
// computes BrokerRole at runtime: Core NATS surfaces a "subject" while
// JetStream surfaces a "stream". Toggling the UseJetStream flag must
// flip the reported role without reconstruction.
public class broker_role_tests
{
    [Fact]
    public void core_nats_endpoint_broker_role_is_subject()
    {
        new NatsEndpoint("orders.created", new NatsTransport(), EndpointRole.Application)
            .BrokerRole.ShouldBe("subject");
    }

    [Fact]
    public void jetstream_endpoint_broker_role_is_stream()
    {
        var endpoint = new NatsEndpoint("orders.created", new NatsTransport(), EndpointRole.Application)
        {
            UseJetStream = true
        };

        endpoint.BrokerRole.ShouldBe("stream");
    }
}
