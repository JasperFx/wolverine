using Amazon.SQS.Model;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

// Per-transport coverage for #2641 on AmazonSqsQueue. SQS inherits raw Endpoint
// rather than the typed Endpoint<TMapper, TConcreteMapper>, so it needs its own
// override of HasCustomEnvelopeMapper. This test pins down both directions
// (override absent / present) so the behavior doesn't drift from the generic
// case.
public class endpoint_descriptor_interop_mode_tests
{
    [Fact]
    public void sqs_queue_with_no_mapper_override_does_not_report_custom()
    {
        var queue = new AmazonSqsQueue("default-q", new AmazonSqsTransport());

        new EndpointDescriptor(queue).InteropMode.ShouldNotBe("Custom");
    }

    [Fact]
    public void sqs_queue_with_custom_mapper_reports_custom_interop_mode()
    {
        var queue = new AmazonSqsQueue("custom-q", new AmazonSqsTransport())
        {
            Mapper = new StubSqsMapper()
        };

        new EndpointDescriptor(queue).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void sqs_queue_with_registered_mapper_factory_reports_custom_interop_mode()
    {
        var queue = new AmazonSqsQueue("custom-q-factory", new AmazonSqsTransport())
        {
            MapperFactory = (_, _) => new StubSqsMapper()
        };

        new EndpointDescriptor(queue).InteropMode.ShouldBe("Custom");
    }

    private sealed class StubSqsMapper : ISqsEnvelopeMapper
    {
        public string BuildMessageBody(Envelope envelope) => string.Empty;
        public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
            => Array.Empty<KeyValuePair<string, MessageAttributeValue>>();
        public void ReadEnvelopeData(Envelope envelope, string messageBody,
            IDictionary<string, MessageAttributeValue> attributes) { }
    }
}
