using Google.Cloud.PubSub.V1;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-5842. <c>ListenToPubsubSubscription()</c> attaches to a subscription Wolverine didn't create, so the
/// <c>IdentifierPrefix</c> must not be applied to its name. The endpoint constructor used to prefix it anyway,
/// because the existing-subscription flag was only set after construction.
/// </summary>
public class existing_subscription_ignores_identifier_prefix_5842
{
    [Fact]
    public void an_existing_subscription_keeps_its_exact_name_under_a_prefix()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine").PrefixIdentifiers("dev");

        options.ListenToPubsubSubscription("orders-sub");

        var transport = options.Transports.GetOrCreate<PubsubTransport>();
        var endpoint = transport.Topics["orders-sub"];

        endpoint.IsExistingSubscription.ShouldBeTrue();
        endpoint.Server.Subscription.Name.ShouldBe(new SubscriptionName("wolverine", "orders-sub"));
        endpoint.Uri.ShouldBe(new Uri("pubsub://wolverine/orders-sub"));
    }

    [Fact]
    public void an_existing_subscription_on_a_named_broker_keeps_its_exact_name_under_a_prefix()
    {
        var options = new WolverineOptions();
        var broker = new BrokerName("americas");
        options.AddNamedPubsubBroker(broker, "wolverine2").PrefixIdentifiers("dev");

        options.ListenToPubsubSubscriptionOnNamedBroker(broker, "orders-sub");

        var transport = options.Transports.GetOrCreate<PubsubTransport>(broker);
        var endpoint = transport.Topics["orders-sub"];

        endpoint.Server.Subscription.Name.ShouldBe(new SubscriptionName("wolverine2", "orders-sub"));
        endpoint.Uri.ShouldBe(new Uri("americas://wolverine2/orders-sub"));
    }

    [Fact]
    public void an_ordinary_topic_listener_still_gets_the_prefix()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine").PrefixIdentifiers("dev");

        options.ListenToPubsubTopic("x");

        var transport = options.Transports.GetOrCreate<PubsubTransport>();
        var endpoint = transport.Topics["dev.x"];

        endpoint.IsExistingSubscription.ShouldBeFalse();
        endpoint.Server.Topic.Name.ShouldBe(new TopicName("wolverine", "dev.x"));
        endpoint.Server.Subscription.Name.ShouldBe(new SubscriptionName("wolverine", "dev.x"));
        endpoint.Uri.ShouldBe(new Uri("pubsub://wolverine/dev.x"));
    }
}
