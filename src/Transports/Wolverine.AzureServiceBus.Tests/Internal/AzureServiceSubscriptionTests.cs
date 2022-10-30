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
        var subscription = new AzureServiceBusSubscription(new AzureServiceBusTransport(), topic, "sub1");
        subscription.Uri.ShouldBe(new Uri("asb://topic/incoming/sub1"));
    }
}