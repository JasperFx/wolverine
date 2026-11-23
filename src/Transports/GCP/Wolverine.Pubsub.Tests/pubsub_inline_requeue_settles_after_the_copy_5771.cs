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
/// GH-5771. Outside NativeAck, a Pub/Sub requeue publishes a COPY of the message and the original is settled by
/// the value the subscriber callback returns. On an Inline endpoint the requeue happens inside that callback, and
/// the callback used to answer Ack no matter what: the copy went through a <c>RetryBlock</c> whose
/// <c>PostAsync</c> only logs a failed publish and queues a background retry, and does nothing at all once the
/// host is stopping. Either way the original was acked while the copy existed in memory only, or nowhere.
/// </summary>
public class pubsub_inline_requeue_settles_after_the_copy_5771 : IAsyncDisposable
{
    private readonly PublisherServiceApiClient thePublisher = Substitute.For<PublisherServiceApiClient>();
    private readonly CancellationTokenSource theRuntimeCancellation = new();
    private readonly List<PublishRequest> thePublished = new();
    private PubsubListener? theListener;

    private PubsubListener buildListener(EndpointMode mode, IReceiver receiver)
    {
        var transport = new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = thePublisher,
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };

        var endpoint = new PubsubEndpoint("gh5771", transport) { Mode = mode };

        var clients = new PubsubClientSet
        {
            ProjectId = "wolverine",
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
            PublisherApiClient = thePublisher,
            SubscriberApiClient = transport.SubscriberApiClient
        };

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Cancellation.Returns(theRuntimeCancellation.Token);

        theListener = new NotPullingPubsubListener(endpoint, transport, receiver, runtime, clients);
        return theListener;
    }

    private void publishingFails()
    {
        thePublisher.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CallSettings>())
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "Pub/Sub publish is down")));
    }

    private void publishingSucceeds()
    {
        thePublisher.PublishAsync(Arg.Do<PublishRequest>(thePublished.Add), Arg.Any<CallSettings>())
            .Returns(new PublishResponse());
    }

    private static PubsubMessage delivery(string orderingKey = "")
    {
        return new PubsubMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Data = ByteString.CopyFromUtf8("{}"),
            OrderingKey = orderingKey,
            Attributes = { ["content-type"] = "application/json", ["message-type"] = "ping" }
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }

        theRuntimeCancellation.Dispose();
    }

    [Fact]
    public async Task an_inline_requeue_whose_copy_fails_to_publish_nacks_the_original()
    {
        publishingFails();
        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(EndpointMode.Inline, receiver);
        receiver.Listener = listener;

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        reply.ShouldBe(SubscriberClient.Reply.Nack,
            "The copy was never published, so the original is the only copy there is. Pub/Sub redelivers it after a Nack.");
    }

    [Fact]
    public async Task an_inline_requeue_while_the_host_is_stopping_still_publishes_the_copy_before_acking()
    {
        publishingSucceeds();
        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(EndpointMode.Inline, receiver);
        receiver.Listener = listener;

        // The RetryBlock does nothing at all once the runtime is cancelled
        await theRuntimeCancellation.CancelAsync();

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        thePublished.ShouldHaveSingleItem();
        reply.ShouldBe(SubscriberClient.Reply.Ack);
    }

    [Fact]
    public async Task an_inline_requeue_acks_the_original_once_the_copy_is_published()
    {
        publishingSucceeds();
        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(EndpointMode.Inline, receiver);
        receiver.Listener = listener;

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        thePublished.ShouldHaveSingleItem();
        reply.ShouldBe(SubscriberClient.Reply.Ack);
    }

    [Theory]
    [InlineData(EndpointMode.Inline)]
    [InlineData(EndpointMode.BufferedInMemory)]
    public async Task the_requeue_copy_keeps_the_ordering_key_it_was_received_with(EndpointMode mode)
    {
        publishingSucceeds();

        // An interop publisher: the ordering key is on the Pub/Sub message only, never in a group-id attribute
        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(mode, receiver);
        receiver.Listener = listener;

        var reply = await listener.ReceiveDeliveryAsync(delivery("customer-42"));
        reply.ShouldBe(SubscriberClient.Reply.Ack);

        var copy = thePublished.ShouldHaveSingleItem().Messages.ShouldHaveSingleItem();
        copy.OrderingKey.ShouldBe("customer-42");
    }

    [Fact]
    public async Task an_explicit_group_id_still_wins_over_the_received_ordering_key()
    {
        publishingSucceeds();
        var receiver = new RequeueingReceiver5771();
        var listener = buildListener(EndpointMode.Inline, receiver);
        receiver.Listener = listener;

        var message = delivery("received-key");
        message.Attributes["group-id"] = "group-1";

        await listener.ReceiveDeliveryAsync(message);

        thePublished.ShouldHaveSingleItem().Messages.ShouldHaveSingleItem().OrderingKey.ShouldBe("group-1");
    }
}

/// <summary>
/// Stands in for the handler pipeline of an Inline receiver whose error policy is <c>Requeue()</c>: every envelope
/// is handed back to the listener while the subscriber callback is still waiting on <c>ReceivedAsync</c>.
/// </summary>
internal class RequeueingReceiver5771 : IReceiver
{
    public IListener Listener { get; set; } = null!;

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        foreach (var envelope in messages) await Listener.DeferAsync(envelope);
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope) => ReceivedAsync(listener, [envelope]);

    public ValueTask DrainAsync() => ValueTask.CompletedTask;

    public IHandlerPipeline Pipeline => null!;

    public void Dispose()
    {
    }
}

/// <summary>A listener that never opens a streaming pull, so a test can drive its subscriber callback directly.</summary>
internal class NotPullingPubsubListener : PubsubListener
{
    public NotPullingPubsubListener(PubsubEndpoint endpoint, PubsubTransport transport, IReceiver receiver,
        IWolverineRuntime runtime, PubsubClientSet clients) : base(endpoint, transport, receiver, runtime, clients)
    {
    }

    public override Task StartAsync() => Task.CompletedTask;
}
