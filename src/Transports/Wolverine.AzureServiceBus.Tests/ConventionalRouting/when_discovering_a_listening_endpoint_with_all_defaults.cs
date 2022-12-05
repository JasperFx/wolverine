using System;
using System.Linq;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext
{
    private readonly Uri theExpectedUri = "asb://queue/routed".ToUri();
    private readonly AzureServiceBusQueue theQueue;

    public when_discovering_a_listening_endpoint_with_all_defaults()
    {
        theQueue = theRuntime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AzureServiceBusQueue>();
    }

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theQueue.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theQueue.ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_buffered_by_default()
    {
        theQueue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}