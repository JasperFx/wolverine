using JasperFx.Core;
using Shouldly;
using TestMessages;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public class broadcasting_conventional_listener_discovery : BroadcastingConventionalRoutingContext
{
    [Fact]
    public void disable_sender_with_lambda()
    {
        ConfigureConventions(c => c.TopicNameForSender(t =>
        {
            if (t == typeof(NewPublishedMessage))
            {
                return null; // should not be routed
            }

            return t.ToMessageTypeName().Replace(".", "-");
        }));

        AssertNoRoutes<NewPublishedMessage>();
    }

    [Fact]
    public void exclude_types()
    {
        ConfigureConventions(c => c.ExcludeTypes(t => t == typeof(NewPublishedMessage)));

        AssertNoRoutes<NewPublishedMessage>();

        var uri = "sqs://newpublished.message".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void include_types()
    {
        ConfigureConventions(c => c.IncludeTypes(t => t == typeof(NewPublishedMessage)));

        AssertNoRoutes<Message1>();

        PublishingRoutesFor<NewPublishedMessage>().Any().ShouldBeTrue();

        var uri = "asb://topic/newpublished.message\"".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void configure_topic_sender_overrides()
    {
        ConfigureConventions(c => c.ConfigureTopicSending((c, _) => c.AddOutgoingRule(new FakeEnvelopeRule())));

        var route = PublishingRoutesFor<BroadcastedMessage>().Single().Sender.Endpoint
            .ShouldBeOfType<AzureServiceBusTopic>();

        route.OutgoingRules.Single().ShouldBeOfType<FakeEnvelopeRule>();
    }

    [Fact]
    public void disable_listener_by_lambda()
    {
        ConfigureConventions(c => c.TopicNameForListener(t =>
        {
            if (t == typeof(NewRoutedMessage))
            {
                return null; // should not be routed
            }

            return t.ToMessageTypeName().Replace(".", "-");
        }));

        var uri = "sqs://routed".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void configure_topic_listener()
    {
        ConfigureConventions(c => c.ConfigureSubscriptionListeners((x, _) => { x.UseDurableInbox(); }));

        var endpoint = theRuntime.Endpoints.EndpointFor("asb://topic/broadcasted/tests".ToUri())
            .ShouldBeOfType<AzureServiceBusSubscription>();

        endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    public class FakeEnvelopeRule : IEnvelopeRule
    {
        public void Modify(Envelope envelope)
        {
            throw new NotImplementedException();
        }
    }
}