using RabbitMQ.Client;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// Per-transport coverage for #2641: when a user wires a custom envelope mapper
// onto a RabbitMQ endpoint, the EndpointDescriptor must report
// InteropMode = "Custom". When no override is wired, the descriptor must NOT
// report "Custom" — it falls through to the serializer-name signal (or null).
//
// Mirrors the in-tree "broker_role_tests" pattern: pure, no docker, no NSubstitute.
public class endpoint_descriptor_interop_mode_tests
{
    [Fact]
    public void rabbit_queue_with_no_mapper_override_does_not_report_custom()
    {
        var queue = new RabbitMqQueue("default-queue", new RabbitMqTransport());

        new EndpointDescriptor(queue).InteropMode.ShouldNotBe("Custom");
    }

    [Fact]
    public void rabbit_queue_with_custom_mapper_reports_custom_interop_mode()
    {
        var queue = new RabbitMqQueue("custom-queue", new RabbitMqTransport())
        {
            EnvelopeMapper = new StubRabbitMqMapper()
        };

        new EndpointDescriptor(queue).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void rabbit_exchange_with_custom_mapper_reports_custom_interop_mode()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("custom-exchange", transport)
        {
            EnvelopeMapper = new StubRabbitMqMapper()
        };

        new EndpointDescriptor(exchange).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void rabbit_topic_endpoint_with_custom_mapper_reports_custom_interop_mode()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("ex", transport);
        var topic = new RabbitMqTopicEndpoint("t", exchange, transport)
        {
            EnvelopeMapper = new StubRabbitMqMapper()
        };

        new EndpointDescriptor(topic).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void rabbit_routing_with_custom_mapper_reports_custom_interop_mode()
    {
        var transport = new RabbitMqTransport();
        var exchange = new RabbitMqExchange("ex", transport);
        var routing = new RabbitMqRouting(exchange, "rk", transport)
        {
            EnvelopeMapper = new StubRabbitMqMapper()
        };

        new EndpointDescriptor(routing).InteropMode.ShouldBe("Custom");
    }

    private sealed class StubRabbitMqMapper : IRabbitMqEnvelopeMapper
    {
        public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing) { }
        public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming) { }
    }
}
