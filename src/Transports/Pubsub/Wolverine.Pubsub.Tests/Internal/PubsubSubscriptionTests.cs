using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests.Internal;

public class PubsubSubscriptionTests {
    private PubsubTransport createTransport() => new("wolverine", new() {
        EmulatorDetection = EmulatorDetection.EmulatorOnly
    }) {
        PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
        SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>()
    };

    [Fact]
    public void default_dead_letter_name_is_transport_default() {
        new PubsubTopic("foo", createTransport()).FindOrCreateSubscription("bar")
            .Options.DeadLetterName.ShouldBe(PubsubTransport.DeadLetterEndpointName);
    }

    [Fact]
    public void default_mode_is_buffered() {
        new PubsubTopic("foo", createTransport()).FindOrCreateSubscription("bar")
            .Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void create_uri() {
        var topic = new PubsubTopic("top1", createTransport());
        var subscription = topic.FindOrCreateSubscription("sub1");

        subscription.Uri.ShouldBe(new Uri($"{PubsubTransport.ProtocolName}://topic/top1/sub1"));
    }

    [Fact]
    public void endpoint_name_is_subscription_name_without_prefix() {
        var topic = new PubsubTopic("top1", createTransport());
        var subscription = topic.FindOrCreateSubscription("sub1");

        subscription.EndpointName.ShouldBe("sub1");
    }

    [Fact]
    public async Task initialize_with_no_auto_provision() {
        var transport = createTransport();
        var topic = new PubsubTopic("foo", transport);
        var subscription = topic.FindOrCreateSubscription("bar");

        await subscription.InitializeAsync(NullLogger.Instance);

        await transport.SubscriberApiClient!.DidNotReceive().CreateSubscriptionAsync(Arg.Any<Subscription>());
    }

    [Fact]
    public async Task initialize_with_auto_provision() {
        var transport = createTransport();

        transport.AutoProvision = true;

        var topic = new PubsubTopic("foo", transport);
        var subscription = topic.FindOrCreateSubscription("bar");

        await subscription.InitializeAsync(NullLogger.Instance);

        await transport.SubscriberApiClient!.Received().CreateSubscriptionAsync(Arg.Is<Subscription>(x =>
            x.SubscriptionName == subscription.SubscriptionName &&
            x.TopicAsTopicName == topic.TopicName
        ));
    }
}
