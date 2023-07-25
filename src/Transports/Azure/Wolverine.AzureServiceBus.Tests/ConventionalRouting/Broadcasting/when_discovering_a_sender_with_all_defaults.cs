using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public class when_discovering_a_sender_with_all_defaults : BroadcastingConventionalRoutingContext
{
    private readonly MessageRoute theTopicRoute;

    public when_discovering_a_sender_with_all_defaults()
    {
        theTopicRoute = PublishingRoutesFor<BroadcastedMessage>().Single();
    }

    [Fact]
    public void should_have_exactly_one_route()
    {
        theTopicRoute.ShouldNotBeNull();
    }

    [Fact]
    public void routed_to_azure_service_bus_topic()
    {
        var topicEndpoint = theTopicRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusTopic>();
        topicEndpoint.TopicName.ShouldBe("broadcasted");
    }

    [Fact]
    public void endpoint_mode_is_buffered_by_default()
    {
        var topicEndpoint = theTopicRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusTopic>();
        topicEndpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}