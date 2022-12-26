using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext
{
    private readonly Uri theExpectedUri = "asb://queue/routedmessage2".ToUri();
    private readonly AzureServiceBusQueue theQueue;

    public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
    {
        ConfigureConventions(c => c.QueueNameForListener(t => t.Name.ToLower() + "2"));

        var theRuntimeEndpoints = theRuntime.Endpoints.ActiveListeners().ToArray();
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
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}