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

    [Fact]
    public void find_topic_with_hierarchical_name_by_uri()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.GetOrCreateEndpoint(
                new Uri("asb://topic/" + Uri.EscapeDataString("szrmgr/myevent")))
            .ShouldBeOfType<AzureServiceBusTopic>();

        topic.TopicName.ShouldBe("szrmgr/myevent");
    }

    [Fact]
    public void find_subscription_on_hierarchical_topic_by_uri()
    {
        var transport = new AzureServiceBusTransport();
        var subscription = transport.GetOrCreateEndpoint(
                new Uri("asb://topic/" + Uri.EscapeDataString("szrmgr/myevent") + "/sub1"))
            .ShouldBeOfType<AzureServiceBusSubscription>();

        subscription.SubscriptionName.ShouldBe("sub1");
        subscription.Topic.TopicName.ShouldBe("szrmgr/myevent");
    }

    [Fact]
    public void hierarchical_topic_creates_correct_uri()
    {
        var transport = new AzureServiceBusTransport();
        var topic = new AzureServiceBusTopic(transport, "szrmgr/myevent");

        // URI should encode the slash so it's not confused with a path separator
        topic.Uri.Host.ShouldBe("topic");
        // Round-trip: resolving by URI should return a topic, not a subscription
        transport.GetOrCreateEndpoint(topic.Uri)
            .ShouldBeOfType<AzureServiceBusTopic>()
            .TopicName.ShouldBe("szrmgr/myevent");
    }

    [Fact]
    public void hierarchical_topic_subscription_creates_correct_uri()
    {
        var transport = new AzureServiceBusTransport();
        var topic = new AzureServiceBusTopic(transport, "szrmgr/myevent");
        var subscription = new AzureServiceBusSubscription(transport, topic, "sub1");

        // Round-trip: resolving by URI should return the subscription
        transport.GetOrCreateEndpoint(subscription.Uri)
            .ShouldBeOfType<AzureServiceBusSubscription>()
            .SubscriptionName.ShouldBe("sub1");
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