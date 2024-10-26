using JasperFx.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext
{
    private readonly PubsubEndpoint theEndpoint;
    private readonly Uri theExpectedUri = $"{PubsubTransport.ProtocolName}://wolverine/routedmessage2".ToUri();

    public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
    {
        ConfigureConventions(c => c.QueueNameForListener(t => t.Name.ToLower() + "2"));

        var theRuntimeEndpoints = theRuntime.Endpoints.ActiveListeners().ToArray();

        theEndpoint = theRuntime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<PubsubEndpoint>();
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
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}