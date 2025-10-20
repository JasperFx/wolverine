using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext
{
    private readonly PubsubSubscription theEndpoint;
    private readonly Uri theExpectedUri = $"{PubsubTransport.ProtocolName}://wolverine/routed".ToUri();

    public when_discovering_a_listening_endpoint_with_all_defaults()
    {
        theEndpoint = theRuntime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<PubsubSubscription>();
    }

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theEndpoint.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theEndpoint.ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_buffered_by_default()
    {
        theEndpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}