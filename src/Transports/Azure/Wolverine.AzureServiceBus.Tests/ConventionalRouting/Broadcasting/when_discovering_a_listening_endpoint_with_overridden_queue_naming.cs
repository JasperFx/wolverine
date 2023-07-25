using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : BroadcastingConventionalRoutingContext
{
    private readonly Uri theExpectedTopicUri = "asb://topic/BroadcastedMessage2/Test2".ToUri();

    private readonly AzureServiceBusSubscription theTopic;

    public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
    {
        ConfigureConventions(c => c.IncludeTypes(t => t == typeof(BroadcastedMessage))
                    .SubscriptionNameForListener(t => "Test2")
                    .TopicNameForListener(t => t.NameInCode() + "2"));

        var theRuntimeEndpoints = theRuntime.Endpoints.ActiveListeners().ToArray();

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
    public void should_be_an_active_listener()
    {
        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedTopicUri)
            .ShouldBeTrue();
    }
}