using System;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class AzureServiceSubscriptionTests
{
    [Fact]
    public void create_uri()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        var subscription = new AzureServiceBusQueueSubscription(new AzureServiceBusTransport(), topic, "sub1");
        subscription.Uri.ShouldBe(new Uri("asb://topic/incoming/sub1"));
    }

    [Fact]
    public void endpoint_name_should_be_subscription_name()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        var subscription = new AzureServiceBusQueueSubscription(new AzureServiceBusTransport(), topic, "sub1");

        subscription.EndpointName.ShouldBe("sub1");
    }
}