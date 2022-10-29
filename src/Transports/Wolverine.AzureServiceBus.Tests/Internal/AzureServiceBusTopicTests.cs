using System;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class AzureServiceBusTopicTests
{
    [Fact]
    public void create_uri()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        topic.Uri.ShouldBe(new Uri("asb://topic/incoming"));
    }

    [Fact]
    public void endpoint_name_should_be_topic_name()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        topic.EndpointName.ShouldBe("incoming");
    }
}