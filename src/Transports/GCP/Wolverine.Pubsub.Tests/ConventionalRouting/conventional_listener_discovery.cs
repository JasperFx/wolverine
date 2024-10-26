using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class conventional_listener_discovery : ConventionalRoutingContext
{
    [Fact]
    public void disable_sender_with_lambda()
    {
        ConfigureConventions(c => c.TopicNameForSender(t =>
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

        var uri = $"{PubsubTransport.ProtocolName}://wolverine/published-message".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);

        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners()
            .Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void include_types()
    {
        ConfigureConventions(c => { c.IncludeTypes(t => t == typeof(PublishedMessage)); });

        AssertNoRoutes<Message1>();

        PublishingRoutesFor<PublishedMessage>().Any().ShouldBeTrue();

        var uri = $"{PubsubTransport.ProtocolName}://wolverine/Message1".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);

        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners()
            .Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void configure_sender_overrides()
    {
        ConfigureConventions(c => c.ConfigureSending((c, _) => c.AddOutgoingRule(new FakeEnvelopeRule())));

        var route = PublishingRoutesFor<PublishedMessage>().Single().As<MessageRoute>().Sender.Endpoint
            .ShouldBeOfType<PubsubEndpoint>();

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

        var uri = $"{PubsubTransport.ProtocolName}://wolverine/routed".ToUri();
        var endpoint = theRuntime.Endpoints.EndpointFor(uri);

        endpoint.ShouldBeNull();

        theRuntime.Endpoints.ActiveListeners()
            .Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public void configure_listener()
    {
        ConfigureConventions(c => c.ConfigureListeners((x, _) => { x.UseDurableInbox(); }));

        var endpoint = theRuntime.Endpoints.EndpointFor($"{PubsubTransport.ProtocolName}://wolverine/routed".ToUri())
            .ShouldBeOfType<PubsubEndpoint>();

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