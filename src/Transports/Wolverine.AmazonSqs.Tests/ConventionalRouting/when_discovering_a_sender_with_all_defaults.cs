using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting
{
    public class when_discovering_a_sender_with_all_defaults : ConventionalRoutingContext
    {
        private readonly MessageRoute theRoute;

        public when_discovering_a_sender_with_all_defaults()
        {
            theRoute = PublishingRoutesFor<PublishedMessage>().Single();
        }

        [Fact]
        public void should_have_exactly_one_route()
        {
            theRoute.ShouldNotBeNull();
        }

        [Fact]
        public void routed_to_sqs_queue()
        {
            var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<AmazonSqsEndpoint>();
            endpoint.QueueName.ShouldBe("published-message");
        }

        [Fact]
        public void endpoint_mode_is_buffered_by_default()
        {
            var endpoint = theRoute.Sender.Endpoint.ShouldBeOfType<AmazonSqsEndpoint>();
            endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        }

    }
}
