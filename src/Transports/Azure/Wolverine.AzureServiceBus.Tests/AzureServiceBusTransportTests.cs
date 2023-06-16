using System;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
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

    [Fact]
    public void retry_and_response_queues_are_enabled_by_default()
    {
        var transport = new AzureServiceBusTransport();
        transport.SystemQueuesEnabled.ShouldBeTrue();
    }

    
    [Fact]
    public void return_all_endpoints_gets_dead_letter_queue_too()
    {
        var transport = new AzureServiceBusTransport();
        var one = transport.Queues["one"];
        var two = transport.Queues["two"];
        var three = transport.Queues["three"];

        one.DeadLetterQueueName = null;
        two.DeadLetterQueueName = "two-dead-letter-queue";

        var endpoints = transport.Endpoints().OfType<AzureServiceBusQueue>().ToArray();

        endpoints.ShouldContain(x => x.QueueName == AzureServiceBusTransport.DeadLetterQueueName);
        endpoints.ShouldContain(x => x.QueueName == "two-dead-letter-queue");
        endpoints.ShouldContain(x => x.QueueName == "one");
        endpoints.ShouldContain(x => x.QueueName == "two");
        endpoints.ShouldContain(x => x.QueueName == "three");
    }
}