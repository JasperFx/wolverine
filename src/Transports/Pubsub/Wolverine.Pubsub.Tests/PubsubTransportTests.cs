using Shouldly;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubTransportTests {
    [Fact]
    public void find_topic_by_uri() {
        var transport = new PubsubTransport("wolverine");
        var topic = transport.GetOrCreateEndpoint(new Uri($"{PubsubTransport.ProtocolName}://one")).ShouldBeOfType<PubsubTopic>();

        topic.Name.TopicId.ShouldBe(PubsubTransport.SanitizePubsubName("one"));
    }

    [Fact]
    public void find_subscription_by_uri() {
        var transport = new PubsubTransport("wolverine");
        var subscription = transport
            .GetOrCreateEndpoint(new Uri($"{PubsubTransport.ProtocolName}://one/red"))
            .ShouldBeOfType<PubsubSubscription>();

        subscription.Name.SubscriptionId.ShouldBe(PubsubTransport.SanitizePubsubName("red"));
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

        // one.Options.DeadLetterName = null;
        // two.Options.DeadLetterName = "two-dead-letter";

        var endpoints = transport.Endpoints().OfType<PubsubSubscription>().ToArray();

        // endpoints.ShouldContain(x => x.Name.SubscriptionId == PubsubTransport.SanitizePubsubName(PubsubTransport.DeadLetterEndpointName));
        // endpoints.ShouldContain(x => x.Name.SubscriptionId == PubsubTransport.SanitizePubsubName("two-dead-letter"));
        endpoints.ShouldContain(x => x.Name.SubscriptionId == PubsubTransport.SanitizePubsubName("one"));
        endpoints.ShouldContain(x => x.Name.SubscriptionId == PubsubTransport.SanitizePubsubName("two"));
        endpoints.ShouldContain(x => x.Name.SubscriptionId == PubsubTransport.SanitizePubsubName("three"));
    }
}
