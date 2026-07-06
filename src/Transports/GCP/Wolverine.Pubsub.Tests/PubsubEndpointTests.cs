using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubEndpointTests
{
    private PubsubTransport createTransport()
    {
        return new PubsubTransport("wolverine")
        {
            PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };
    }

    [Fact]
    public void default_dead_letter_name_is_null()
    {
        new PubsubEndpoint("foo", createTransport())
            .DeadLetterName.ShouldBeNull();
    }

    [Fact]
    public void default_mode_is_buffered()
    {
        new PubsubEndpoint("foo", createTransport())
            .Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void default_endpoint_name_is_topic_name()
    {
        new PubsubEndpoint("top1", createTransport())
            .EndpointName.ShouldBe("top1");
    }

    [Fact]
    public void uri()
    {
        new PubsubEndpoint("top1", createTransport())
            .Uri.ShouldBe(new Uri($"{PubsubTransport.ProtocolName}://wolverine/top1"));
    }

    [Fact]
    public async Task initialize_with_no_auto_provision()
    {
        var transport = createTransport();
        var topic = new PubsubEndpoint("foo", transport);

        await topic.InitializeAsync(NullLogger.Instance);

        await transport.PublisherApiClient!.DidNotReceive().CreateTopicAsync(Arg.Any<TopicName>());
    }

    [Fact]
    public async Task competing_consumer_listener_gets_per_node_subscription()
    {
        var transport = createTransport();
        transport.AssignedNodeNumber = 5;

        var endpoint = new PubsubEndpoint("foo", transport)
        {
            IsListener = true,
            ListenerScope = ListenerScope.CompetingConsumers
        };

        await endpoint.SetupAsync(NullLogger.Instance);

        // Competing consumers should each read from their own per-node subscription
        endpoint.Server.Subscription.Name.SubscriptionId.ShouldBe("foo.5");
    }

    [Fact]
    public async Task leader_pinned_listener_uses_a_single_shared_subscription()
    {
        var transport = createTransport();
        transport.AssignedNodeNumber = 5;

        var endpoint = new PubsubEndpoint("foo", transport)
        {
            IsListener = true,
            ListenerScope = ListenerScope.PinnedToLeader
        };

        await endpoint.SetupAsync(NullLogger.Instance);

        // A leader-pinned listener must NOT get a per-node subscription name, otherwise every
        // node creates its own subscription and Pub/Sub fans a copy of every message to each,
        // breaking the single-consumer (leader-only) guarantee.
        endpoint.Server.Subscription.Name.SubscriptionId.ShouldBe("foo");
    }

    [Fact]
    public async Task initialize_with_auto_provision()
    {
        var transport = createTransport();

        transport.AutoProvision = true;

        var topic = new PubsubEndpoint("foo", transport);

        transport.PublisherApiClient!.GetTopicAsync(Arg.Is(topic.Server.Topic.Name)).Throws<Exception>();

        await topic.InitializeAsync(NullLogger.Instance);

        await transport.PublisherApiClient!.Received().CreateTopicAsync(Arg.Is(new Topic
        {
            TopicName = topic.Server.Topic.Name,
            MessageRetentionDuration = topic.Server.Topic.Options.MessageRetentionDuration
        }));
    }
}