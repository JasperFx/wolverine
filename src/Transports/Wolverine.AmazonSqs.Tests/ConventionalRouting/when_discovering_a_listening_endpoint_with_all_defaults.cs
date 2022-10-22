using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting
{
    public class when_discovering_a_listening_endpoint_with_all_defaults : ConventionalRoutingContext
    {
        private readonly Uri theExpectedUri = "sqs://routed".ToUri();
        private readonly AmazonSqsEndpoint theEndpoint;

        public when_discovering_a_listening_endpoint_with_all_defaults()
        {
            theEndpoint = theRuntime.Endpoints.EndpointFor(theExpectedUri).ShouldBeOfType<AmazonSqsEndpoint>();
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
        public void mode_is_buffered_by_default()
        {
            theEndpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        }

        [Fact]
        public void should_be_an_active_listener()
        {
            theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
                .ShouldBeTrue();
        }

    }
}
