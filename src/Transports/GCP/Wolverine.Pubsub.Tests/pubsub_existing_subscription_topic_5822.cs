using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-5822. A listener on an existing subscription (<c>ListenToPubsubSubscription()</c>) used to take the
/// subscription's name as its topic. It now knows the topic the subscription really belongs to -- configured with
/// <c>FromTopic()</c>, or read from the subscription at startup -- and when it cannot know it, a requeue never
/// publishes to a topic that does not exist.
/// </summary>
public class pubsub_existing_subscription_topic_5822 : IAsyncDisposable
{
    private readonly PublisherServiceApiClient thePublisher = Substitute.For<PublisherServiceApiClient>();
    private readonly SubscriberServiceApiClient theSubscriber = Substitute.For<SubscriberServiceApiClient>();
    private readonly List<PublishRequest> thePublished = new();
    private readonly CancellationTokenSource theRuntimeCancellation = new();
    private readonly PubsubTransport theTransport;
    private PubsubListener? theListener;

    public pubsub_existing_subscription_topic_5822()
    {
        theTransport = new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = thePublisher,
            SubscriberApiClient = theSubscriber,
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };

        thePublisher.PublishAsync(Arg.Do<PublishRequest>(thePublished.Add), Arg.Any<CallSettings>())
            .Returns(new PublishResponse());
    }

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }

        theRuntimeCancellation.Dispose();
    }

    private PubsubEndpoint existingSubscription(string subscriptionId = "orders-sub")
    {
        var options = new WolverineOptions();
        options.Transports.Add(theTransport);
        options.ListenToPubsubSubscription(subscriptionId);

        return theTransport.Topics[subscriptionId];
    }

    private void theSubscriptionBelongsTo(string topic)
    {
        theSubscriber.GetSubscriptionAsync(Arg.Any<SubscriptionName>(), Arg.Any<CallSettings>())
            .Returns(new Subscription
            {
                Name = new SubscriptionName("wolverine", "orders-sub").ToString(),
                Topic = topic
            });
    }

    private void theSubscriptionCannotBeRead()
    {
        theSubscriber.GetSubscriptionAsync(Arg.Any<SubscriptionName>(), Arg.Any<CallSettings>())
            .ThrowsAsync(new RpcException(new Status(StatusCode.PermissionDenied,
                "User not authorized to perform this action.")));
    }

    private PubsubListener buildListener(PubsubEndpoint endpoint, IReceiver receiver)
    {
        var clients = new PubsubClientSet
        {
            ProjectId = "wolverine",
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
            PublisherApiClient = thePublisher,
            SubscriberApiClient = theSubscriber
        };

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Cancellation.Returns(theRuntimeCancellation.Token);

        theListener = new NotPullingPubsubListener(endpoint, theTransport, receiver, runtime, clients);
        return theListener;
    }

    private static PubsubMessage delivery()
    {
        return new PubsubMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Data = ByteString.CopyFromUtf8("{}"),
            Attributes = { ["content-type"] = "application/json", ["message-type"] = "ping" }
        };
    }

    [Fact]
    public async Task the_topic_is_read_from_the_subscription_at_startup()
    {
        theSubscriptionBelongsTo("projects/wolverine/topics/orders");
        var endpoint = existingSubscription();

        await endpoint.InitializeAsync(NullLogger.Instance);

        endpoint.KnowsItsTopic.ShouldBeTrue();
        endpoint.Server.Topic.Name.ShouldBe(new TopicName("wolverine", "orders"));
        endpoint.TopicNameFor("wolverine").ShouldBe(new TopicName("wolverine", "orders"));

        // The subscription and the endpoint's identity are untouched
        endpoint.Server.Subscription.Name.ShouldBe(new SubscriptionName("wolverine", "orders-sub"));
        endpoint.Uri.ShouldBe(new Uri("pubsub://wolverine/orders-sub"));
    }

    [Fact]
    public async Task a_topic_in_another_project_is_kept_whole()
    {
        theSubscriptionBelongsTo("projects/upstream/topics/orders");
        var endpoint = existingSubscription();

        await endpoint.InitializeAsync(NullLogger.Instance);

        endpoint.TopicNameFor("wolverine").ShouldBe(new TopicName("upstream", "orders"));
    }

    [Fact]
    public async Task resolving_the_topic_never_provisions_anything()
    {
        theSubscriptionBelongsTo("projects/wolverine/topics/orders");
        theTransport.AutoProvision = true;
        var endpoint = existingSubscription();

        await endpoint.InitializeAsync(NullLogger.Instance);

        await thePublisher.DidNotReceiveWithAnyArgs().CreateTopicAsync(default(Topic)!, default(CallSettings));
        await theSubscriber.DidNotReceiveWithAnyArgs()
            .CreateSubscriptionAsync(default(Subscription)!, default(CallSettings));
    }

    [Fact]
    public async Task an_explicit_topic_wins_and_the_subscription_is_never_read()
    {
        theSubscriptionBelongsTo("projects/wolverine/topics/something-else");

        var options = new WolverineOptions();
        options.Transports.Add(theTransport);
        var configuration = options.ListenToPubsubSubscription("orders-sub").FromTopic("orders");
        ((IDelayedEndpointConfiguration)configuration).Apply();

        var endpoint = theTransport.Topics["orders-sub"];
        endpoint.ExplicitSubscriptionTopic.ShouldBe(new TopicName("wolverine", "orders"));

        await endpoint.InitializeAsync(NullLogger.Instance);

        endpoint.Server.Topic.Name.ShouldBe(new TopicName("wolverine", "orders"));
        endpoint.KnowsItsTopic.ShouldBeTrue();
        await theSubscriber.DidNotReceiveWithAnyArgs().GetSubscriptionAsync(default(SubscriptionName)!, default(CallSettings));
    }

    [Fact]
    public void an_explicit_topic_can_be_a_full_resource_name_in_another_project()
    {
        var options = new WolverineOptions();
        options.Transports.Add(theTransport);
        var configuration = options.ListenToPubsubSubscription("orders-sub").FromTopic("projects/upstream/topics/orders");
        ((IDelayedEndpointConfiguration)configuration).Apply();

        theTransport.Topics["orders-sub"].ExplicitSubscriptionTopic.ShouldBe(new TopicName("upstream", "orders"));
    }

    [Fact]
    public void an_explicit_topic_is_refused_on_a_listener_that_owns_its_topic()
    {
        var options = new WolverineOptions();
        options.Transports.Add(theTransport);
        var configuration = options.ListenToPubsubTopic("orders").FromTopic("other");

        Should.Throw<InvalidOperationException>(() => ((IDelayedEndpointConfiguration)configuration).Apply());
    }

    [Fact]
    public async Task an_unreadable_subscription_leaves_the_topic_unknown_without_failing_startup()
    {
        theSubscriptionCannotBeRead();
        var endpoint = existingSubscription();

        await endpoint.InitializeAsync(NullLogger.Instance);

        endpoint.KnowsItsTopic.ShouldBeFalse();
    }

    [Fact]
    public async Task a_subscription_whose_topic_was_deleted_leaves_the_topic_unknown()
    {
        theSubscriptionBelongsTo("_deleted-topic_");
        var endpoint = existingSubscription();

        await endpoint.InitializeAsync(NullLogger.Instance);

        endpoint.KnowsItsTopic.ShouldBeFalse();
    }

    [Fact]
    public async Task nothing_is_published_through_an_endpoint_whose_topic_is_unknown()
    {
        theSubscriptionCannotBeRead();
        var endpoint = existingSubscription();
        await endpoint.InitializeAsync(NullLogger.Instance);

        await Should.ThrowAsync<WolverinePubsubTransportException>(() =>
            endpoint.SendMessageAsync(new Envelope { Data = [1], ContentType = "application/json" }, NullLogger.Instance));

        thePublished.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_inline_requeue_with_an_unknown_topic_nacks_the_original_and_publishes_nothing()
    {
        theSubscriptionCannotBeRead();
        var endpoint = existingSubscription();
        endpoint.Mode = EndpointMode.Inline;
        await endpoint.InitializeAsync(NullLogger.Instance);

        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(endpoint, receiver);
        receiver.Listener = listener;

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        reply.ShouldBe(SubscriberClient.Reply.Nack, "Pub/Sub redelivers the original to this subscription");
        thePublished.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_buffered_requeue_with_an_unknown_topic_is_handed_back_for_a_local_requeue()
    {
        theSubscriptionCannotBeRead();
        var endpoint = existingSubscription();
        endpoint.Mode = EndpointMode.BufferedInMemory;
        await endpoint.InitializeAsync(NullLogger.Instance);

        var listener = buildListener(endpoint, Substitute.For<IReceiver>());

        var requeued = await listener.TryRequeueAsync(new Envelope { Data = [1], ContentType = "application/json" });

        requeued.ShouldBeFalse("the receiver requeues it in memory rather than losing it to a publish that cannot succeed");
        thePublished.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(EndpointMode.Inline)]
    [InlineData(EndpointMode.BufferedInMemory)]
    public async Task a_requeue_copy_goes_to_the_resolved_topic(EndpointMode mode)
    {
        theSubscriptionBelongsTo("projects/wolverine/topics/orders");
        var endpoint = existingSubscription();
        endpoint.Mode = mode;
        await endpoint.InitializeAsync(NullLogger.Instance);

        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(endpoint, receiver);
        receiver.Listener = listener;

        var reply = await listener.ReceiveDeliveryAsync(delivery());
        reply.ShouldBe(SubscriberClient.Reply.Ack);

        // The buffered copy goes through a background RetryBlock
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (thePublished.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        thePublished.ShouldHaveSingleItem().TopicAsTopicName.ShouldBe(new TopicName("wolverine", "orders"));
    }
}
