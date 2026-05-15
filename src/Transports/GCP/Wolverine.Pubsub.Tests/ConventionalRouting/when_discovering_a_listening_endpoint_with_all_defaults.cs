using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext, IAsyncLifetime
{
    private PubsubEndpoint theEndpoint = null!;
    private readonly Uri theExpectedUri = $"{PubsubTransport.ProtocolName}://wolverine/routed".ToUri();

    public async Task InitializeAsync()
    {
        theEndpoint = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<PubsubEndpoint>();
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
    public void mode_is_buffered_by_default()
    {
        theEndpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
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
