using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// GH-3270: RabbitMQ sending endpoints (exchange + routing key) were under-populated in the endpoint-health snapshot —
// blank Name and "Unknown" status. RabbitMqRouting now sets a recognizable EndpointName, and RabbitMQ senders report
// a broker-connection-aware status via IReportConnectionState (so consumers see Connected/Disconnected, not Unknown).
public class routing_endpoint_name_and_status_3270
{
    [Fact]
    public void named_exchange_routing_combines_exchange_and_routing_key()
    {
        var transport = new RabbitMqTransport();
        var routing = transport.Exchanges["orders"].Routings["high-priority"];

        routing.EndpointName.ShouldBe("orders/high-priority");
    }

    [Fact]
    public void default_exchange_routing_uses_the_routing_key_as_the_name()
    {
        // The default exchange routes by routing key, which is the target queue name — so that name alone is the
        // most recognizable identity (rather than "default/<key>").
        var transport = new RabbitMqTransport();
        var routing = transport.Exchanges[TransportConstants.Default].Routings["target-queue"];

        routing.EndpointName.ShouldBe("target-queue");
    }

    [Fact]
    public void routing_endpoint_name_is_not_the_synthetic_uri()
    {
        var transport = new RabbitMqTransport();
        var routing = transport.Exchanges["orders"].Routings["high"];

        routing.EndpointName.ShouldNotContain("routing/");
        routing.EndpointName.ShouldNotBe(routing.Uri.ToString());
    }

    [Fact]
    public void rabbitmq_senders_report_a_broker_connection_aware_status()
    {
        // RabbitMqSender derives from RabbitMqChannelAgent, which implements IReportConnectionState — so the
        // endpoint-health snapshot resolves Connected/Disconnected for senders rather than Unknown.
        typeof(IReportConnectionState).IsAssignableFrom(typeof(RabbitMqSender)).ShouldBeTrue();
        typeof(IReportConnectionState).IsAssignableFrom(typeof(RabbitMqChannelAgent)).ShouldBeTrue();
    }
}
