using Azure.Messaging.ServiceBus;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// Per-transport coverage for #2641: when a user wires a custom envelope mapper
// onto an Azure Service Bus endpoint, the EndpointDescriptor must report
// InteropMode = "Custom".
public class endpoint_descriptor_interop_mode_tests
{
    [Fact]
    public void asb_queue_with_no_mapper_override_does_not_report_custom()
    {
        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "q");
        new EndpointDescriptor(queue).InteropMode.ShouldNotBe("Custom");
    }

    [Fact]
    public void asb_queue_with_custom_mapper_reports_custom_interop_mode()
    {
        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "q")
        {
            EnvelopeMapper = new StubAsbMapper()
        };

        new EndpointDescriptor(queue).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void asb_topic_with_custom_mapper_reports_custom_interop_mode()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "t")
        {
            EnvelopeMapper = new StubAsbMapper()
        };

        new EndpointDescriptor(topic).InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void asb_subscription_with_custom_mapper_reports_custom_interop_mode()
    {
        var transport = new AzureServiceBusTransport();
        var topic = new AzureServiceBusTopic(transport, "t");
        var subscription = new AzureServiceBusSubscription(transport, topic, "s")
        {
            EnvelopeMapper = new StubAsbMapper()
        };

        new EndpointDescriptor(subscription).InteropMode.ShouldBe("Custom");
    }

    private sealed class StubAsbMapper : IAzureServiceBusEnvelopeMapper
    {
        public void MapEnvelopeToOutgoing(Envelope envelope, ServiceBusMessage outgoing) { }
        public void MapIncomingToEnvelope(Envelope envelope, ServiceBusReceivedMessage incoming) { }
    }
}
