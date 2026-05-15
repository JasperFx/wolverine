using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext
{
    private MessageRoute theRoute = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        theRoute = (await PublishingRoutesFor<PublishedMessage>()).Single().As<MessageRoute>();
    }

    [Fact]
    public void should_have_exactly_one_route()
    {
        theRoute.ShouldNotBeNull();
    }

    [Fact]
    public void routed_to_azure_service_bus_queue()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusQueue>();
        endpoint.QueueName.ShouldBe("published.message");
    }

    [Fact]
    public void endpoint_mode_is_buffered_by_default()
    {
        var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<AzureServiceBusQueue>();
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
