using JasperFx.Core;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext, IAsyncLifetime
{
    private readonly Uri theExpectedUri = "sqs://routed".ToUri();
    private AmazonSqsQueue theQueue = null!;

    public async Task InitializeAsync()
    {
        theQueue = (await theRuntime()).Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AmazonSqsQueue>();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

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
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
