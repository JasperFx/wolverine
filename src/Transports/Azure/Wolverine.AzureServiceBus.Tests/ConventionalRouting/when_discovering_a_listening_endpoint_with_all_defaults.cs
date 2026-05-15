using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext
{
    private readonly Uri theExpectedUri = "asb://queue/routed2".ToUri();

    [Fact]
    public async Task endpoint_should_be_a_listener()
    {
        var theQueue = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AzureServiceBusQueue>();
        theQueue.IsListener.ShouldBeTrue();
    }

    [Fact]
    public async Task endpoint_should_not_be_null()
    {
        var theQueue = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AzureServiceBusQueue>();
        theQueue.ShouldNotBeNull();
    }

    [Fact]
    public async Task mode_is_buffered_by_default()
    {
        var theQueue = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AzureServiceBusQueue>();
        theQueue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
