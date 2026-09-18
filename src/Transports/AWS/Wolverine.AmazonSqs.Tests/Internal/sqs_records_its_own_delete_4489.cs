using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-4489. <c>AmazonSqsEnvelope.WasDeleted</c> was read by the requeue block and assigned nowhere in the
/// repo, so that guard could never fire: a requeue whose send failed re-ran the whole block and deleted the
/// original a second time.
///
/// <para>The flag is also how SQS answers "did I settle this delivery?", which matters beyond the wasted
/// round trip -- SQS is one of only two transports (GCP Pub/Sub is the other) whose dead letter move sends a
/// COPY and leaves the original unsettled, so callers that must tell a terminal move from a copy have
/// nothing else to read. See GH-4481 for the Azure Service Bus half of the same contract.</para>
/// </summary>
public class sqs_records_its_own_delete_4489 : IAsyncDisposable
{
    private const string TheQueueUrl = "http://localhost:4566/000000000000/gh4489";

    private readonly IAmazonSQS theClient = Substitute.For<IAmazonSQS>();
    private readonly AmazonSqsTransport theTransport = new();
    private SqsListener? theListener;

    private async Task<SqsListener> buildListenerAsync()
    {
        theTransport.Client = theClient;

        var queue = theTransport.Queues["gh4489"];

        // QueueUrl only has a private setter, assigned from the broker's response -- so hand the substitute
        // a CreateQueue response and let SetupAsync populate it the way the real startup path does.
        theClient.CreateQueueAsync(Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateQueueResponse { QueueUrl = TheQueueUrl });
        await queue.SetupAsync(theClient);

        // Batching would defer the delete onto a channel and make "was it deleted once" a timing question.
        // One-at-a-time routes straight at IAmazonSQS.DeleteMessageAsync, which is what these assert on.
        queue.DeleteMessageBatchSize = 1;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());

        theListener = new SqsListener(runtime, queue, theTransport, Substitute.For<IReceiver>());
        return theListener;
    }

    private static AmazonSqsEnvelope envelopeFor(string receiptHandle)
    {
        return new AmazonSqsEnvelope(new Message { MessageId = receiptHandle, ReceiptHandle = receiptHandle });
    }

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }
    }

    [Fact]
    public async Task completing_an_envelope_records_that_it_was_deleted()
    {
        var listener = await buildListenerAsync();
        var envelope = envelopeFor("receipt-1");

        await listener.CompleteAsync(envelope);

        await theClient.Received(1).DeleteMessageAsync(TheQueueUrl, "receipt-1", Arg.Any<CancellationToken>());

        envelope.WasDeleted.ShouldBeTrue(
            "Nothing assigned this before GH-4489, so the requeue block's !WasDeleted guard never fired.");

        // The cross-transport half of the same question, which MessageContext.CompleteAsync short-circuits
        // on and RabbitMQ and Azure Service Bus both report.
        envelope.HasBeenAcked.ShouldBeTrue();
    }

    [Fact]
    public async Task completing_the_same_envelope_twice_only_deletes_once()
    {
        var listener = await buildListenerAsync();
        var envelope = envelopeFor("receipt-2");

        await listener.CompleteAsync(envelope);
        await listener.CompleteAsync(envelope);

        await theClient.Received(1).DeleteMessageAsync(TheQueueUrl, "receipt-2", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The requeue path deletes the original and puts a COPY back on the queue, so it records the delete but
    /// must NOT claim a terminal. Per GH-3710 the idempotency layer reads <c>HasBeenAcked</c> to decide
    /// whether an id may be remembered as processed, and remembering a requeued id would silently discard
    /// the very redelivery this path just arranged.
    /// </summary>
    [Fact]
    public async Task a_requeue_records_the_delete_but_is_not_an_ack()
    {
        theClient.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResponse());

        var listener = await buildListenerAsync();
        var envelope = envelopeFor("receipt-3");

        await listener.DeferAsync(envelope);

        await theClient.Received(1).DeleteMessageAsync(TheQueueUrl, "receipt-3", Arg.Any<CancellationToken>());

        envelope.WasDeleted.ShouldBeTrue();
        envelope.HasBeenAcked.ShouldBeFalse(
            "A requeue is not a terminal -- remembering its id would discard the redelivery it just sent.");
    }
}
