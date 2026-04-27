using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// Locks down GH-2601 for the Azure Service Bus endpoints.
public class broker_role_tests
{
    [Fact]
    public void queue_broker_role_is_queue()
    {
        var transport = new AzureServiceBusTransport();
        new AzureServiceBusQueue(transport, "q").BrokerRole.ShouldBe("queue");
    }

    [Fact]
    public void topic_broker_role_is_topic()
    {
        var transport = new AzureServiceBusTransport();
        new AzureServiceBusTopic(transport, "t").BrokerRole.ShouldBe("topic");
    }

    [Fact]
    public void subscription_broker_role_is_subscription()
    {
        var transport = new AzureServiceBusTransport();
        var topic = new AzureServiceBusTopic(transport, "t");
        new AzureServiceBusSubscription(transport, topic, "s").BrokerRole.ShouldBe("subscription");
    }
}
