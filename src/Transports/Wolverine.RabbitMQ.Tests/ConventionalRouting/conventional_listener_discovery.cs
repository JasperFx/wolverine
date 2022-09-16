using System.Linq;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting
{
    public class conventional_listener_discovery : ConventionalRoutingContext
    {
        [Fact]
        public void disable_sender_with_lambda()
        {
            ConfigureConventions(c => c.ExchangeNameForSending(t =>
            {
                if (t == typeof(PublishedMessage)) return null; // should not be routed

                return t.ToMessageTypeName();
            }));

            AssertNoRoutes<PublishedMessage>();

        }

        public class FakeEnvelopeRule : IEnvelopeRule
        {
            public void Modify(Envelope envelope)
            {
                throw new System.NotImplementedException();
            }
        }

        [Fact]
        public void configure_sender_overrides()
        {
            ConfigureConventions(c => c.ConfigureSending((e, c) => c.Endpoint.OutgoingRules.Add(new FakeEnvelopeRule())));

            var route = PublishingRoutesFor<PublishedMessage>().Single().Sender.Endpoint
                .ShouldBeOfType<RabbitMqEndpoint>();

            route.OutgoingRules.Single().ShouldBeOfType<FakeEnvelopeRule>();
        }

        [Fact]
        public void disable_listener_by_lambda()
        {
            ConfigureConventions(c => c.QueueNameForListener(t =>
            {
                if (t == typeof(RoutedMessage)) return null; // should not be routed

                return t.ToMessageTypeName();
            }));

            var uri = "rabbitmq://queue/routed".ToUri();
            var endpoint = theRuntime.EndpointFor(uri);
            endpoint.ShouldBeNull();

            theRuntime.ActiveListeners().Any(x => x.Uri == uri)
                .ShouldBeFalse();
        }

        [Fact]
        public void configure_listener()
        {
            ConfigureConventions(c => c.ConfigureListener((queue, context) =>
            {
                context.Endpoint.ListenerCount = 6;
            }));

            var endpoint = theRuntime.EndpointFor("rabbitmq://queue/routed".ToUri())
                .ShouldBeOfType<RabbitMqEndpoint>();

            endpoint.ListenerCount.ShouldBe(6);
        }

        [Fact]
        public void override_mode_to_durable()
        {
            ConfigureConventions(c => c.InboxedListenersAndOutboxedSenders());

            var listeners = theRuntime.ActiveListeners().Where(x => x.Uri.Scheme == RabbitMqTransport.ProtocolName);
            listeners.Any().ShouldBeTrue();
            foreach (var listener in listeners)
            {
                listener.Endpoint.Mode.ShouldBe(EndpointMode.Durable);
            }
        }

        [Fact]
        public void override_mode_to_inline()
        {
            ConfigureConventions(c => c.InlineListenersAndSenders());

            var listeners = theRuntime.ActiveListeners().Where(x => x.Uri.Scheme == RabbitMqTransport.ProtocolName);
            listeners.Any().ShouldBeTrue();
            foreach (var listener in listeners)
            {
                listener.Endpoint.Mode.ShouldBe(EndpointMode.Inline);
            }
        }



    }
}
