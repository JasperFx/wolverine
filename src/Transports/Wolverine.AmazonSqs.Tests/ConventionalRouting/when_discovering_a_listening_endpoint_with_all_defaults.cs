using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting
{
    public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext
    {
        private readonly Uri theExpectedUri = "sqs://routed".ToUri();
        private readonly AmazonSqsQueue theQueue;

        public when_discovering_a_listening_endpoint_with_all_defaults()
        {
            theQueue = theRuntime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AmazonSqsQueue>();
        }

        [Fact]
        public void endpoint_should_be_a_listener()
        {
            theQueue.IsListener.ShouldBeTrue();
        }

        [Fact]
        public void endpoint_should_not_be_null()
        {
            theQueue.ShouldNotBeNull();
        }

        [Fact]
        public void mode_is_buffered_by_default()
        {
            theQueue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        }

        [Fact]
        public void should_be_an_active_listener()
        {
            theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
                .ShouldBeTrue();
        }

    }
}
