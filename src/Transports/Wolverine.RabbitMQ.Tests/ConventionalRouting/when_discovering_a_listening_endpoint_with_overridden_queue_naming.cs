using System;
using System.Linq;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting
{
    public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext
    {
        private readonly Uri theExpectedUri = "rabbitmq://queue/routed2".ToUri();
        private readonly RabbitMqEndpoint theEndpoint;

        public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
        {
            ConfigureConventions(c => c.QueueNameForListener(t => t.ToMessageTypeName() + "2"));

            theEndpoint = theRuntime.EndpointFor(theExpectedUri).ShouldBeOfType<RabbitMqEndpoint>();
        }

        [Fact]
        public void endpoint_should_be_a_listener()
        {
            theEndpoint.IsListener.ShouldBeTrue();
        }

        [Fact]
        public void endpoint_should_not_be_null()
        {
            theEndpoint.ShouldNotBeNull();
        }

        [Fact]
        public void should_be_an_active_listener()
        {
            theRuntime.ActiveListeners().Any(x => x.Uri == theExpectedUri)
                .ShouldBeTrue();
        }

        [Fact]
        public void the_queue_was_declared()
        {
            theTransport.Queues.Has("routed2").ShouldBeTrue();
            theTransport.Queues["routed2"].HasDeclared.ShouldBeTrue();
        }
    }
}
