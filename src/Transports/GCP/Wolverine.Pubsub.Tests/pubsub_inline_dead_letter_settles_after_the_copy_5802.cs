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
/// GH-5802. With a native dead-letter topic, moving a message to it publishes a copy there, and the original is
/// settled by the value the subscriber callback returns. On an Inline endpoint the move happens inside that
/// callback, which used to answer Ack no matter what: the copy went through a <c>RetryBlock</c> whose
/// <c>PostAsync</c> only logs a failed publish and queues a background retry, and does nothing once the host is
/// stopping. Now the dead-letter publish is retried inline for a small, fixed budget, and the original is nacked
/// -- kept by Pub/Sub -- when it never succeeds.
/// </summary>
public class pubsub_inline_dead_letter_settles_after_the_copy_5802 : IAsyncDisposable
{
    private const string DeadLetterTopic = "gh5802-dlq";

    private readonly PublisherServiceApiClient thePublisher = Substitute.For<PublisherServiceApiClient>();
    private readonly CancellationTokenSource theRuntimeCancellation = new();
    private readonly List<PublishRequest> theAttempts = new();
    private PubsubListener? theListener;
    private readonly DeadLetteringReceiver5802 theReceiver = new();

    private PubsubListener buildListener(EndpointMode mode = EndpointMode.Inline)
    {
        var transport = new PubsubTransport
        {
            ProjectId = "wolverine",
            PublisherApiClient = thePublisher,
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };

        var endpoint = new PubsubEndpoint("gh5802", transport) { Mode = mode, DeadLetterName = DeadLetterTopic };

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
    public async Task a_dead_letter_publish_that_fails_once_is_retried_inline_and_then_acks()
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
    public async Task a_dead_letter_publish_that_keeps_failing_nacks_the_original()
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
    public async Task the_host_stopping_during_the_retry_nacks_the_original()
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
    public async Task a_dead_letter_publish_that_succeeds_acks_the_original()
    {
        publishing(_ => new PublishResponse());
        var listener = buildListener();

        var reply = await listener.ReceiveDeliveryAsync(delivery());

        theAttempts.Count.ShouldBe(1);
        reply.ShouldBe(SubscriberClient.Reply.Ack);
    }
}

/// <summary>
/// Stands in for the handler pipeline of an Inline receiver whose error policy moves every message to the native
/// dead-letter topic while the subscriber callback is still waiting on <c>ReceivedAsync</c>.
/// </summary>
internal class DeadLetteringReceiver5802 : IReceiver
{
    public PubsubListener Listener { get; set; } = null!;

    public List<DeadLetterMoveFailedException> MoveFailures { get; } = new();

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        foreach (var envelope in messages)
        {
            try
            {
                await Listener.MoveToErrorsAsync(envelope, new InvalidOperationException("handler failed"));
            }
            catch (DeadLetterMoveFailedException e)
            {
                // What MoveToErrorQueue catches: the move failed and the delivery was left with the broker, so the
                // envelope must not be completed
                MoveFailures.Add(e);
            }
        }
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope) => ReceivedAsync(listener, [envelope]);

    public ValueTask DrainAsync() => ValueTask.CompletedTask;

    public IHandlerPipeline Pipeline => null!;

    public void Dispose()
    {
    }
}
