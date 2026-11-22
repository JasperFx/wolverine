using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-5753 and GH-5752. SQS has no native "put it back", so a requeue sends a COPY and settles the original.
/// It used to delete the original first: a copy whose send failed, or a process that exited in between, lost
/// the message. NativeAck endpoints take this path for every queued envelope on a graceful shutdown.
///
/// <para>On a FIFO queue the copy also has to survive SQS's own deduplication. Reusing the original's
/// explicit <c>MessageDeduplicationId</c> made SQS drop it inside the 5 minute window while still answering
/// the send with success, and a raw JSON envelope never carried the received <c>MessageGroupId</c>, so SQS
/// rejected the copy outright.</para>
/// </summary>
public class sqs_requeue_sends_before_it_deletes_5753 : IAsyncDisposable
{
    private readonly IAmazonSQS theClient = Substitute.For<IAmazonSQS>();
    private readonly AmazonSqsTransport theTransport = new();
    private SqsListener? theListener;

    private async Task<SqsListener> buildListenerAsync(string queueName)
    {
        theTransport.Client = theClient;

        var queue = theTransport.Queues[queueName];
        theClient.CreateQueueAsync(Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateQueueResponse { QueueUrl = $"http://localhost:4566/000000000000/{queueName}" });
        await queue.SetupAsync(theClient);

        // One-at-a-time deletes go straight at IAmazonSQS.DeleteMessageAsync, so the order is observable
        queue.DeleteMessageBatchSize = 1;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());

        theListener = new SqsListener(runtime, queue, theTransport, Substitute.For<IReceiver>());
        return theListener;
    }

    // A received envelope always carries its serialized body, which the requeue copy is built from
    private static AmazonSqsEnvelope envelopeFor(Message message)
    {
        return new AmazonSqsEnvelope(message)
        {
            Data = "{}"u8.ToArray(),
            MessageType = "ping",
            ContentType = "application/json"
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }
    }

    [Fact]
    public async Task a_requeue_whose_send_fails_leaves_the_original_on_the_queue()
    {
        theClient.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonSQSException("SendMessage is down"));

        var listener = await buildListenerAsync("gh5753-send-fails");
        var envelope = envelopeFor(new Message { MessageId = "m-1", ReceiptHandle = "receipt-1" });

        // The first attempt runs inline in PostAsync, so it has failed by the time DeferAsync returns
        await listener.DeferAsync(envelope);

        await theClient.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), "receipt-1", Arg.Any<CancellationToken>());
        envelope.WasDeleted.ShouldBeFalse(
            "The copy was never accepted, so the original is the only copy there is. SQS redelivers it after the visibility timeout.");
    }

    [Fact]
    public async Task a_requeue_sends_the_copy_before_it_deletes_the_original()
    {
        theClient.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResponse());

        var listener = await buildListenerAsync("gh5753-order");
        var envelope = envelopeFor(new Message { MessageId = "m-2", ReceiptHandle = "receipt-2" });

        await listener.DeferAsync(envelope);

        Received.InOrder(() =>
        {
            _ = theClient.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
            _ = theClient.DeleteMessageAsync(Arg.Any<string>(), "receipt-2", Arg.Any<CancellationToken>());
        });

        envelope.WasDeleted.ShouldBeTrue();

        // GH-3710: a requeue is not a terminal
        envelope.HasBeenAcked.ShouldBeFalse();
    }

    [Fact]
    public async Task a_fifo_requeue_copy_gets_its_own_deduplication_id_and_keeps_the_received_message_group()
    {
        var requests = new List<SendMessageRequest>();
        theClient.SendMessageAsync(Arg.Do<SendMessageRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResponse());

        var listener = await buildListenerAsync("gh5752.fifo");

        // What a raw JSON listener hands over: the explicit deduplication id the sender used, and the
        // message group only as the SQS system attribute, never on Envelope.GroupId
        var envelope = envelopeFor(new Message
        {
            MessageId = "m-3",
            ReceiptHandle = "receipt-3",
            Attributes = new Dictionary<string, string> { ["MessageGroupId"] = "group-1" }
        });
        envelope.DeduplicationId = "ping-1";

        await listener.DeferAsync(envelope);

        var copy = requests.ShouldHaveSingleItem();
        copy.MessageGroupId.ShouldBe("group-1");
        copy.MessageDeduplicationId.ShouldNotBeNullOrEmpty();
        copy.MessageDeduplicationId.ShouldNotBe("ping-1",
            "SQS drops a send that reuses a deduplication id inside the 5 minute window, yet answers it with success");
    }
}
