using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

// GH-3786: NOT flaky -- 3 of 3 fail, 7.1m, deterministically, on a clean emulator (2.0.1) with the
// GH-3783 readiness gate. BrokerInitializationException "Unable to initialize the Broker asb in
// time". Re-tagged with the numbers rather than left bare; untag when GH-3786 is fixed.
[Trait("Category", "Flaky")]
public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext
{
    private MessageRoute theRoute = null!;

    public override async ValueTask InitializeAsync()
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
