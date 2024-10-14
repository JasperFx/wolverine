using Shouldly;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubTransportTests {
    [Fact]
    public void find_topic_by_uri() {
        var transport = new PubsubTransport("wolverine");
        var topic = transport.GetOrCreateEndpoint(new Uri($"{PubsubTransport.ProtocolName}://topic/one")).ShouldBeOfType<PubsubTopic>();

        topic.TopicName.TopicId.ShouldBe("one");
    }

    [Fact]
    public void find_subscription_by_uri() {
        var transport = new PubsubTransport("wolverine");
        var subscription = transport
            .GetOrCreateEndpoint(new Uri($"{PubsubTransport.ProtocolName}://topic/one/red"))
            .ShouldBeOfType<PubsubSubscription>();

        subscription.SubscriptionName.SubscriptionId.ShouldBe("red");
    }

    [Fact]
    public void retry_and_response_queues_are_enabled_by_default() {
        var transport = new PubsubTransport("wolverine");

        transport.SystemEndpointsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void return_all_endpoints_gets_dead_letter_subscription_too() {
        var transport = new PubsubTransport("wolverine");
        var one = transport.Topics["one"].FindOrCreateSubscription();
        var two = transport.Topics["two"].FindOrCreateSubscription();
        var three = transport.Topics["three"].FindOrCreateSubscription();

        one.Options.DeadLetterName = null;
        two.Options.DeadLetterName = "two-dead-letter-queue";

        var endpoints = transport.Endpoints().OfType<PubsubSubscription>().ToArray();

        endpoints.ShouldContain(x => x.SubscriptionName.SubscriptionId == PubsubTransport.DeadLetterEndpointName);
        endpoints.ShouldContain(x => x.SubscriptionName.SubscriptionId == "two-dead-letter-queue");
        endpoints.ShouldContain(x => x.SubscriptionName.SubscriptionId == "one");
        endpoints.ShouldContain(x => x.SubscriptionName.SubscriptionId == "two");
        endpoints.ShouldContain(x => x.SubscriptionName.SubscriptionId == "three");
    }
}
