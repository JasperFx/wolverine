using JasperFx.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext, IAsyncLifetime
{
    private PubsubEndpoint theEndpoint = null!;
    private readonly Uri theExpectedUri = $"{PubsubTransport.ProtocolName}://wolverine/routedmessage2".ToUri();

    public async Task InitializeAsync()
    {
        await ConfigureConventions(c => c.QueueNameForListener(t => t.Name.ToLower() + "2"));

        var runtime = await theRuntime();
        var theRuntimeEndpoints = runtime.Endpoints.ActiveListeners().ToArray();

        theEndpoint = runtime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<PubsubEndpoint>();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

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
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints
            .ActiveListeners()
            .Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
