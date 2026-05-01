using Confluent.Kafka;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;
using Xunit;

namespace Wolverine.Kafka.Tests;

// Per-transport coverage for #2641: when a user wires a custom envelope mapper
// onto a Kafka topic endpoint, the EndpointDescriptor must report
// InteropMode = "Custom".
public class endpoint_descriptor_interop_mode_tests
{
    [Fact]
    public void kafka_topic_with_no_mapper_override_does_not_report_custom()
    {
        var topic = new KafkaTopic(new KafkaTransport(), "t", EndpointRole.Application);

        new EndpointDescriptor(topic).InteropMode.ShouldNotBe("Custom");
    }

    [Fact]
    public void kafka_topic_with_custom_mapper_reports_custom_interop_mode()
    {
        var topic = new KafkaTopic(new KafkaTransport(), "t", EndpointRole.Application)
        {
            EnvelopeMapper = new StubKafkaMapper()
        };

        new EndpointDescriptor(topic).InteropMode.ShouldBe("Custom");
    }

    private sealed class StubKafkaMapper : IKafkaEnvelopeMapper
    {
        public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing) { }
        public void MapIncomingToEnvelope(Envelope envelope, Message<string, byte[]> incoming) { }
    }
}
