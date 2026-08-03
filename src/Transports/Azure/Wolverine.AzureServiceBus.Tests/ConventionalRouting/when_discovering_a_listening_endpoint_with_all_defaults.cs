using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

// All four facts are about the SAME discovered listener, so they share one host rather than starting
// Wolverine and provisioning the emulator four times over. See ConventionalRoutingFixture, GH-3786.
public class when_discovering_a_listening_endpoint_with_all_defaults(DefaultConventionalRoutingFixture fixture)
    : IClassFixture<DefaultConventionalRoutingFixture>
{
    private readonly Uri theExpectedUri = "asb://queue/routed2".ToUri();

    private AzureServiceBusQueue theQueue()
        => fixture.theRuntime().Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AzureServiceBusQueue>();

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theQueue().IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theQueue().ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_buffered_by_default()
    {
        theQueue().Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void should_be_an_active_listener()
    {
        fixture.theRuntime().Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
