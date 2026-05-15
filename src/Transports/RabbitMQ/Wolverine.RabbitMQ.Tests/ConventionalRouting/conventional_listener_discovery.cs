using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class conventional_listener_discovery : ConventionalRoutingContext
{
    [Fact]
    public async Task disable_sender_with_lambda()
    {
        await ConfigureConventions(c =>
            {
                c.IncludeTypes(c => c == typeof(PublishedMessage));
                c.ExchangeNameForSending(t =>
                {
                    if (t == typeof(PublishedMessage))
                    {
                        return null; // should not be routed
                    }

                    return t.ToMessageTypeName();
                });
            }
          );

        await AssertNoRoutes<PublishedMessage>();
    }

    [Fact]
    public async Task exclude_types()
    {
        await ConfigureConventions(c =>
        {
            c.ExcludeTypes(t => t == typeof(PublishedMessage) || t == typeof(HeadersMessage));
        });

        await AssertNoRoutes<PublishedMessage>();

        var uri = "rabbitmq://queue/published.message".ToUri();
        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        runtime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task include_types()
    {
        await ConfigureConventions(c => { c.IncludeTypes(t => t == typeof(PublishedMessage)); });

        await AssertNoRoutes<Message1>();

        (await PublishingRoutesFor<PublishedMessage>()).Any().ShouldBeTrue();

        var uri = "rabbitmq://queue/Message1".ToUri();
        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        runtime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task configure_sender_overrides()
    {
        await ConfigureConventions(c =>
            {
                c.IncludeTypes(t => t == typeof(PublishedMessage));
                c.ConfigureSending((c, _) => c.AddOutgoingRule(new FakeEnvelopeRule()));
            }
           );

        var route = (await PublishingRoutesFor<PublishedMessage>()).Single().As<MessageRoute>().Sender.Endpoint
            .ShouldBeOfType<RabbitMqExchange>();

        route.OutgoingRules.Single().ShouldBeOfType<FakeEnvelopeRule>();
    }

    [Fact]
    public async Task disable_listener_by_lambda()
    {
        await ConfigureConventions(c =>
        {
            c.IncludeTypes(t => t == typeof(ConventionallyRoutedMessage));
            c.QueueNameForListener(t =>
            {
                if (t == typeof(ConventionallyRoutedMessage))
                {
                    return null; // should not be routed
                }

                return t.ToMessageTypeName();
            });
        });

        var uri = "rabbitmq://queue/routed".ToUri();
        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor(uri);
        endpoint.ShouldBeNull();

        runtime.Endpoints.ActiveListeners().Any(x => x.Uri == uri)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task configure_listener()
    {
        await ConfigureConventions(c =>
        {
            c.IncludeTypes(t => t == typeof(ConventionallyRoutedMessage));
            c.ConfigureListeners((x, _) => { x.ListenerCount(6); });
        });

        var runtime = await theRuntime();
        var endpoint = runtime.Endpoints.EndpointFor("rabbitmq://queue/routed".ToUri())
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
