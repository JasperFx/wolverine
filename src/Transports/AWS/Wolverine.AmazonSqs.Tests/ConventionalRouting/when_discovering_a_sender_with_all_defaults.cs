using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
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
    public void routed_to_sqs_queue()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<AmazonSqsQueue>();
        endpoint.QueueName.ShouldBe("published-message");
    }

    [Fact]
    public void endpoint_mode_is_buffered_by_default()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<AmazonSqsQueue>();
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
