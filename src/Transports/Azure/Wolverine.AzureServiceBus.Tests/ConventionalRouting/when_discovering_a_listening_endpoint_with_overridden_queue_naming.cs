using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

// One host for all three assertions -- the queue-naming override is the same for every one of them.
// See ConventionalRoutingFixture, GH-3786.
public class when_discovering_a_listening_endpoint_with_overridden_queue_naming(OverriddenQueueNamingFixture fixture)
    : IClassFixture<OverriddenQueueNamingFixture>
{
    private readonly Uri theExpectedUri = "asb://queue/routedmessage2".ToUri();

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
    public void should_be_an_active_listener()
    {
        fixture.theRuntime().Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
