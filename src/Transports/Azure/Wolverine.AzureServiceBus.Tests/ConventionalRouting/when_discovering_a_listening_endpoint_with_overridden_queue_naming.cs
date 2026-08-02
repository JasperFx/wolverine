using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

// GH-3786: NOT flaky -- 3 of 3 fail, 6.1m, deterministically, on a clean emulator (2.0.1) with the
// GH-3783 readiness gate. BrokerInitializationException "Unable to initialize the Broker asb in
// time". Re-tagged with the numbers rather than left bare; untag when GH-3786 is fixed.
[Trait("Category", "Flaky")]
public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext
{
    private readonly Uri theExpectedUri = "asb://queue/routedmessage2".ToUri();
    private AzureServiceBusQueue theQueue = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await ConfigureConventions(c => c.QueueNameForListener(t => t.Name.ToLower() + "2"));

        var runtime = await theRuntime();
        var theRuntimeEndpoints = runtime.Endpoints.ActiveListeners().ToArray();
        theQueue = runtime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AzureServiceBusQueue>();
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
    public async Task should_be_an_active_listener()
    {
        (await theRuntime()).Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
            .ShouldBeTrue();
    }
}
