using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-5843. The NativeAck counterpart of GH-5802. A NativeAck endpoint holds the subscriber callback open until every
/// envelope of the delivery is settled, and a move to the native dead-letter topic settles it with an Ack. The
/// dead-letter copy used to go through a <c>RetryBlock</c> that never throws, so the held delivery was acked even
/// when the publish failed outright. Now the publish is retried inline with the same bounded budget, and a delivery
/// whose copy was never published is nacked -- kept by Pub/Sub.
/// </summary>
public class pubsub_native_ack_dead_letter_settles_after_the_copy_5843 : IAsyncDisposable
{
    private const string DeadLetterTopic = "gh5843-dlq";

    private readonly PublisherServiceApiClient thePublisher = Substitute.For<PublisherServiceApiClient>();
    private readonly CancellationTokenSource theRuntimeCancellation = new();
    private readonly List<PublishRequest> theAttempts = new();
    private PubsubListener? theListener;
    private readonly DeadLetteringReceiver5802 theReceiver = new();

    private PubsubListener buildListener(EndpointMode mode = EndpointMode.NativeAck)
    {
        var transport = new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = thePublisher,
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };

        var endpoint = new PubsubEndpoint("gh5843", transport) { Mode = mode, DeadLetterName = DeadLetterTopic };

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

        theListener = new NotPullingPubsubListener(endpoint, transport, theReceiver, runtime, clients);
        theReceiver.Listener = theListener;

        return theListener;
    }

    /// <summary>Every dead-letter publish runs <paramref name="outcome" /> with its 1-based attempt number.</summary>
    private void publishing(Func<int, PublishResponse> outcome)
    {
        thePublisher.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CallSettings>())
            .Returns(call =>
            {
                lock (theAttempts)
                {
                    theAttempts.Add(call.Arg<PublishRequest>());
                    return Task.FromResult(outcome(theAttempts.Count));
                }
            });
    }

    private static RpcException unavailable() => new(new Status(StatusCode.Unavailable, "Pub/Sub publish is down"));

    private static PubsubMessage delivery()
    {
        return new PubsubMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Data = ByteString.CopyFromUtf8("{}"),
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
    public async Task a_held_delivery_whose_dead_letter_publish_fails_once_is_retried_inline_and_then_acks()
    {
        publishing(attempt => attempt == 1 ? throw unavailable() : new PublishResponse());
        var listener = buildListener();

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        // Both attempts happened before the callback answered, not in a background retry afterwards
        theAttempts.Count.ShouldBe(2);
        theAttempts.ShouldAllBe(x => x.Topic.EndsWith("/topics/" + DeadLetterTopic));
        theReceiver.MoveFailures.ShouldBeEmpty();
        reply.ShouldBe(SubscriberClient.Reply.Ack);
    }

    [Fact]
    public async Task a_held_delivery_whose_dead_letter_publish_keeps_failing_is_nacked()
    {
        publishing(_ => throw unavailable());
        var listener = buildListener();

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        reply.ShouldBe(SubscriberClient.Reply.Nack,
            "The dead-letter copy was never published, so acking the original would lose the message");
        theAttempts.Count.ShouldBe(PubsubListener.DeadLetterPublishAttempts);

        // So the error policy neither completes the envelope nor treats it as dead lettered
        theReceiver.MoveFailures.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_host_stopping_during_the_retry_nacks_the_held_delivery()
    {
        publishing(_ =>
        {
            // The host starts stopping while the first attempt is in flight
            theRuntimeCancellation.Cancel();
            throw unavailable();
        });
        var listener = buildListener();

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        reply.ShouldBe(SubscriberClient.Reply.Nack);
        theAttempts.Count.ShouldBe(1, "a stopping host gives up instead of waiting out the backoff");
        theReceiver.MoveFailures.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_held_delivery_whose_dead_letter_publish_succeeds_is_acked()
    {
        publishing(_ => new PublishResponse());
        var listener = buildListener();

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        theAttempts.Count.ShouldBe(1);
        reply.ShouldBe(SubscriberClient.Reply.Ack);
    }
}
