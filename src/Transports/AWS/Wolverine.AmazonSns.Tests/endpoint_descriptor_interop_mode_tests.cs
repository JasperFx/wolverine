using Amazon.SimpleNotificationService.Model;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

// Per-transport coverage for #2641 on AmazonSnsTopic. SNS inherits raw Endpoint
// rather than the typed Endpoint<TMapper, TConcreteMapper>, so it needs its own
// override of HasCustomEnvelopeMapper.
public class endpoint_descriptor_interop_mode_tests
{
    [Fact]
    public void sns_topic_with_no_mapper_override_does_not_report_custom()
    {
        var topic = new AmazonSnsTopic("default-t", new AmazonSnsTransport());

        new EndpointDescriptor(topic).InteropMode.ShouldNotBe("Custom");
    }

    [Fact]
    public void sns_topic_with_custom_mapper_reports_custom_interop_mode()
    {
        var topic = new AmazonSnsTopic("custom-t", new AmazonSnsTransport())
        {
            Mapper = new StubSnsMapper()
        };

        new EndpointDescriptor(topic).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void sns_topic_with_registered_mapper_factory_reports_custom_interop_mode()
    {
        var topic = new AmazonSnsTopic("custom-t-factory", new AmazonSnsTransport())
        {
            MapperFactory = (_, _) => new StubSnsMapper()
        };

        new EndpointDescriptor(topic).InteropMode.ShouldBe("Custom");
    }

    private sealed class StubSnsMapper : ISnsEnvelopeMapper
    {
        public string BuildMessageBody(Envelope envelope) => string.Empty;
        public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
            => Array.Empty<KeyValuePair<string, MessageAttributeValue>>();
        public void ReadEnvelopeData(Envelope envelope, string messageBody,
            IDictionary<string, MessageAttributeValue> attributes) { }
    }
}
