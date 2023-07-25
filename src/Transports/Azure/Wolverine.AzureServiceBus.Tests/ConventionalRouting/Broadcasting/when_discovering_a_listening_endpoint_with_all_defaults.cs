using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public class when_discovering_a_listening_endpoint_with_all_defaults : BroadcastingConventionalRoutingContext
{
    private readonly Uri theExpectedTopicUri = "asb://topic/broadcasted/tests".ToUri();

    private readonly AzureServiceBusSubscription theTopic;

    public when_discovering_a_listening_endpoint_with_all_defaults()
    {
        theTopic = theRuntime.Endpoints.EndpointFor(theExpectedTopicUri).ShouldBeOfType<AzureServiceBusSubscription>();
    }

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theTopic.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theTopic.ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_buffered_by_default()
    {
        theTopic.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedTopicUri)
            .ShouldBeTrue();
    }
}