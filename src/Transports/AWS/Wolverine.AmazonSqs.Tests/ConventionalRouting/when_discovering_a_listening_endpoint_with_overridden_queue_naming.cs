using JasperFx.Core;
using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext, IAsyncLifetime
{
    private readonly Uri theExpectedUri = "sqs://routedmessage2".ToUri();
    private AmazonSqsQueue theQueue = null!;

    public async Task InitializeAsync()
    {
        await ConfigureConventions(c => c.QueueNameForListener(t => t.Name.ToLower() + "2"));

        var runtime = await theRuntime();
        var theRuntimeEndpoints = runtime.Endpoints.ActiveListeners().ToArray();
        theQueue = runtime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AmazonSqsQueue>();
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
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
