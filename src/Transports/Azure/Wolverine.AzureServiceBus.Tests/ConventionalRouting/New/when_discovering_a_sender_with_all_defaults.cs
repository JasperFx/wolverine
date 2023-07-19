using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

public class when_discovering_a_sender_with_all_defaults : NewConventionalRoutingContext
{
    private readonly MessageRoute theQueueRoute;
    private readonly MessageRoute theTopicRoute;

    public when_discovering_a_sender_with_all_defaults()
    {
        theQueueRoute = PublishingRoutesFor<NewPublishedMessage>().Single();
        theTopicRoute = PublishingRoutesFor<BroadcastedMessage>().Single();
    }

    [Fact]
    public void should_have_exactly_one_route()
    {
        theQueueRoute.ShouldNotBeNull();
        theTopicRoute.ShouldNotBeNull();
    }

    [Fact]
    public void routed_to_azure_service_bus_queue()
    {
        var endpoint = theQueueRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusQueue>();
        endpoint.QueueName.ShouldBe("newpublished.message");

        var topicEndpoint = theTopicRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusTopic>();
        topicEndpoint.TopicName.ShouldBe("broadcasted");
    }

    [Fact]
    public void endpoint_mode_is_buffered_by_default()
    {
        var endpoint = theQueueRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusQueue>();
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);

        var topicEndpoint = theTopicRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusTopic>();
        topicEndpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}