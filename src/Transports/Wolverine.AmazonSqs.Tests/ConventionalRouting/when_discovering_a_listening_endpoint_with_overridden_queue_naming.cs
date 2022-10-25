using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting
{
    public class when_discovering_a_listening_endpoint_with_overridden_queue_naming : ConventionalRoutingContext
    {
        private readonly Uri theExpectedUri = "sqs://routedmessage2".ToUri();
        private readonly AmazonSqsQueue theQueue;

        public when_discovering_a_listening_endpoint_with_overridden_queue_naming()
        {
            ConfigureConventions(c => c.QueueNameForListener(t => t.Name.ToLower() + "2"));

            var theRuntimeEndpoints = theRuntime.Endpoints.ActiveListeners().ToArray();
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
        public void should_be_an_active_listener()
        {
            theRuntime.Endpoints.ActiveListeners().Any(x => x.Uri == theExpectedUri)
                .ShouldBeTrue();
        }

    }
}
