using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext
{
    private readonly MessageRoute theRoute;

    public when_discovering_a_sender_with_all_defaults()
    {
        theRoute = PublishingRoutesFor<PublishedMessage>().Single().As<MessageRoute>();
    }

    [Fact]
    public void should_have_exactly_one_route()
    {
        theRoute.ShouldNotBeNull();
    }

    [Fact]
    public void routed_to_pubsub_endpoint()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<PubsubTopic>();

        endpoint.TopicId.ShouldBe("published-message");
    }

    [Fact]
    public void endpoint_mode_is_inline_by_default()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<PubsubTopic>();

        endpoint.Mode.ShouldBe(EndpointMode.Inline);
    }
}