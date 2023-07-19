using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : NewConventionalRoutingContext
{
    private readonly Uri theExpectedQueueUri = "asb://queue/NewRoutedMessage2".ToUri();
    private readonly Uri theExpectedTopicUri = "asb://topic/BroadcastedMessage2/Test2".ToUri();

    private readonly AzureServiceBusQueue theQueue;
    private readonly AzureServiceBusSubscription theTopic;

    public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
    {
        ConfigureConventions(c => c.UsePublishingBroadcastFor(t => t == typeof(BroadcastedMessage), t => "Test2")
            .IdentifierForListener(t => t.NameInCode() + "2"));

        var theRuntimeEndpoints = theRuntime.Endpoints.ActiveListeners().ToArray();

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
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedQueueUri)
            .ShouldBeTrue();
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedTopicUri)
            .ShouldBeTrue();
    }
}