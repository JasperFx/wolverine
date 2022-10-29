using System;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class AzureServiceBusTransportTests
{
    [Fact]
    public void find_queue_by_uri()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.GetOrCreateEndpoint(new Uri("asb://queue/one"))
            .ShouldBeOfType<AzureServiceBusQueue>();
        
        queue.QueueName.ShouldBe("one");
    }
    
    [Fact]
    public void find_topic_by_uri()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.GetOrCreateEndpoint(new Uri("asb://topic/one"))
            .ShouldBeOfType<AzureServiceBusTopic>();
        
        topic.TopicName.ShouldBe("one");
    }
    
    [Fact]
    public void find_subscription_by_uri()
    {
        var transport = new AzureServiceBusTransport();
        var subscription = transport.GetOrCreateEndpoint(new Uri("asb://topic/one/red"))
            .ShouldBeOfType<AzureServiceBusSubscription>();
        
        subscription.SubscriptionName.ShouldBe("red");
    }
}