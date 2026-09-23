using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-4517: the Uri guard and both of the "wrong direction on this endpoint" refusals
/// threw with no message at all.
/// </summary>
public class transport_message_quality_4517
{
    [Fact]
    public void uri_with_an_unrecognized_shape_names_all_three_accepted_forms()
    {
        var transport = new AzureServiceBusTransport();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            transport.TryGetEndpoint(new Uri("asb://nonsense/orders")));

        ex.Message.ShouldContain("asb://queue/{queueName}");
        ex.Message.ShouldContain("asb://topic/{topicName}");
        ex.Message.ShouldContain("asb://topic/{topicName}/{subscriptionName}");
        ex.Message.ShouldContain("asb://nonsense/orders");
    }

    [Fact]
    public async Task listening_to_a_topic_points_at_the_subscription()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["events"];

        var ex = await Should.ThrowAsync<NotSupportedException>(async () =>
            await topic.BuildListenerAsync(null!, null!));

        ex.Message.ShouldContain("events");
        ex.Message.ShouldContain("publish-only");
        ex.Message.ShouldContain("ListenToAzureServiceBusSubscription");
    }

    [Fact]
    public void sending_to_a_subscription_points_at_the_parent_topic()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["events"];
        var subscription = new SubscriptionThatExposesCreateSender(transport, topic, "audit");

        var ex = Should.Throw<NotSupportedException>(() => subscription.TryToCreateSender());

        ex.Message.ShouldContain("audit");
        ex.Message.ShouldContain("listen-only");
        ex.Message.ShouldContain("ToAzureServiceBusTopic(\"events\")");
    }

    // CreateSender() is protected, and the public StartSending() path needs a live IWolverineRuntime.
    // The refusal itself is what's under test, so reach it directly.
    private class SubscriptionThatExposesCreateSender : AzureServiceBusSubscription
    {
        public SubscriptionThatExposesCreateSender(AzureServiceBusTransport parent, AzureServiceBusTopic topic,
            string subscriptionName) : base(parent, topic, subscriptionName)
        {
        }

        public void TryToCreateSender() => CreateSender(null!);
    }
}
