using Shouldly;
using TestMessages;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class conventional_listener_discovery : ConventionalRoutingContext
{
    [Fact]
    public void disable_sender_with_lambda()
    {
        ConfigureConventions(c => c.ExchangeNameForSending(t =>
        {
            if (t == typeof(PublishedMessage))
            {
                return null; // should not be routed
            }

            return t.ToMessageTypeName();
        }));

        AssertNoRoutes<PublishedMessage>();
    }

    [Fact]
    public void exclude_types()
    {
        ConfigureConventions(c => { c.ExcludeTypes(t => t == typeof(PublishedMessage)); });

        AssertNoRoutes<PublishedMessage>();

        var uri = "rabbitmq://queue/published.message".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void include_types()
    {
        ConfigureConventions(c => { c.IncludeTypes(t => t == typeof(PublishedMessage)); });

        AssertNoRoutes<Message1>();

        PublishingRoutesFor<PublishedMessage>().Any().ShouldBeTrue();

        var uri = "rabbitmq://queue/Message1".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void configure_sender_overrides()
    {
        ConfigureConventions(c => c.ConfigureSending((c, _) => c.AddOutgoingRule(new FakeEnvelopeRule())));

        var route = PublishingRoutesFor<PublishedMessage>().Single().Sender.Endpoint
            .ShouldBeOfType<RabbitMqExchange>();

        route.OutgoingRules.Single().ShouldBeOfType<FakeEnvelopeRule>();
    }

    [Fact]
    public void disable_listener_by_lambda()
    {
        ConfigureConventions(c => c.QueueNameForListener(t =>
        {
            if (t == typeof(RoutedMessage))
            {
                return null; // should not be routed
            }

            return t.ToMessageTypeName();
        }));

        var uri = "rabbitmq://queue/routed".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void configure_listener()
    {
        ConfigureConventions(c => c.ConfigureListeners((x, _) => { x.ListenerCount(6); }));

        var endpoint = theRuntime.Endpoints.EndpointFor("rabbitmq://queue/routed".ToUri())
            .ShouldBeOfType<RabbitMqQueue>();

        endpoint.ListenerCount.ShouldBe(6);
    }

    public class FakeEnvelopeRule : IEnvelopeRule
    {
        public void Modify(Envelope envelope)
        {
            throw new NotImplementedException();
        }
    }
}