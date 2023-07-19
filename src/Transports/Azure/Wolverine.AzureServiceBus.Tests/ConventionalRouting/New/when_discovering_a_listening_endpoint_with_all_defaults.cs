using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

public class when_discovering_a_listening_endpoint_with_all_defaults : NewConventionalRoutingContext
{
    private readonly Uri theExpectedQueueUri = "asb://queue/newrouted".ToUri();
    private readonly Uri theExpectedTopicUri = "asb://topic/broadcasted/test".ToUri();

    private readonly AzureServiceBusQueue theQueue;
    private readonly AzureServiceBusSubscription theTopic;

    public when_discovering_a_listening_endpoint_with_all_defaults()
    {
        theQueue = theRuntime.Endpoints.EndpointFor(theExpectedQueueUri).ShouldBeOfType<AzureServiceBusQueue>();
        theTopic = theRuntime.Endpoints.EndpointFor(theExpectedTopicUri).ShouldBeOfType<AzureServiceBusSubscription>();
    }

    [Fact]
    public void endpoint_should_be_a_listener()
    {
        theQueue.IsListener.ShouldBeTrue();
        theTopic.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void endpoint_should_not_be_null()
    {
        theQueue.ShouldNotBeNull();
        theTopic.ShouldNotBeNull();
    }

    [Fact]
    public void mode_is_buffered_by_default()
    {
        theQueue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        theTopic.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedQueueUri)
            .ShouldBeTrue();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedTopicUri)
            .ShouldBeTrue();
    }
}