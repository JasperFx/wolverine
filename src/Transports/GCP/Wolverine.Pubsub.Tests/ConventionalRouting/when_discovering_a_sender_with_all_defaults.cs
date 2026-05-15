using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext, IAsyncLifetime
{
    private MessageRoute theRoute = null!;

    public async Task InitializeAsync()
    {
        theRoute = (await PublishingRoutesFor<PublishedMessage>()).Single().As<MessageRoute>();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void should_have_exactly_one_route()
    {
        theRoute.ShouldNotBeNull();
    }

    [Fact]
    public void routed_to_pubsub_endpoint()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<PubsubEndpoint>();

        endpoint.Server.Topic.Name.TopicId.ShouldBe("published-message");
    }

    [Fact]
    public void endpoint_mode_is_buffered_by_default()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<PubsubEndpoint>();

        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
