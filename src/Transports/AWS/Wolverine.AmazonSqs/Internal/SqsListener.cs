using Amazon.SQS.Model;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener, ISupportDeadLetterQueue, IReportReceiveLoopHealth
{
    private readonly RetryBlock<Envelope>? _deadLetterBlock;
    private readonly AmazonSqsQueue? _deadLetterQueue;
    private readonly AmazonSqsQueue _queue;
    private readonly IReceiver _receiver;
    private readonly RetryBlock<AmazonSqsEnvelope> _requeueBlock;
    private readonly BackgroundReceiveLoop _loop;
    private readonly AmazonSqsTransport _transport;
    private readonly ISqsEnvelopeMapper _mapper;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger _logger;

    // GH-3493 (SO1): completion used to be one DeleteMessage HTTP round trip per message, so a
    // single 10-message receive was paid for with 10 sequential deletes. These coalesce into
    // DeleteMessageBatch calls of up to 10 -- 10x fewer round trips and 10x fewer billable API
    // calls. Null when the endpoint opts out with DeleteMessageBatchSize = 1.
    private readonly BatchingChannel<Message>? _deleteBatching;
    private readonly Block<Message[]>? _deleteBlock;
    private readonly RetryBlock<Message> _singleDeleteBlock;

    // GH-3926: reassembles messages that the sender had to split across several SQS messages.
    // Deliberately unconditional rather than gated on FragmentOversizedMessages -- the fragment
    // framing is unambiguous, so a listener reads it whether or not this same endpoint would send
    // that way, and an asymmetrically configured pair still works.
    private readonly SqsFragmentReassembler _reassembler;

    public SqsListener(IWolverineRuntime runtime, AmazonSqsQueue queue, AmazonSqsTransport transport,
        IReceiver receiver)
    {
        if (transport.Client == null)
        {
            throw new InvalidOperationException("Parent transport has not been initialized");
        }

        _mapper = queue.BuildMapper(runtime);

        _drainTimeout = runtime.DurabilitySettings.DrainTimeout;

        var logger = runtime.LoggerFactory.CreateLogger<SqsListener>();
        _logger = logger;
        _queue = queue;
        _transport = transport;
        _receiver = receiver;

        if (_queue.DeadLetterQueueName != null && !transport.DisableDeadLetterQueues)
        {
            NativeDeadLetterQueueEnabled = true;
            _deadLetterQueue = _transport.Queues[_queue.DeadLetterQueueName];

            // GH-3926: a listener that accepts fragmented messages has to be able to dead letter one too.
            // The dead letter queue is Wolverine's own, so there is no interop reason to make this a
            // second opt-in, and without it an oversized message could be reassembled and handled but
            // never moved to errors.
            if (_queue.FragmentOversizedMessages)
            {
                _deadLetterQueue.FragmentOversizedMessages = true;
            }
        }

        _requeueBlock = new RetryBlock<AmazonSqsEnvelope>(async (env, _) =>
        {
            if (!env.WasDeleted)
            {
                await CompleteAsync(env.SqsMessages);
            }

            await sendOrDiscardAsync(_queue, env, "requeue");
        }, runtime.LoggerFactory.CreateLogger<SqsListener>(), runtime.Cancellation);

        _deadLetterBlock =
            new RetryBlock<Envelope>(async (e, _) => { await sendOrDiscardAsync(_deadLetterQueue!, e, "dead letter"); },
                logger, runtime.Cancellation);

        _reassembler = new SqsFragmentReassembler(queue.FragmentReassemblyTimeout, logger);

        _singleDeleteBlock = new RetryBlock<Message>(
            (message, token) => _transport.Client!.DeleteMessageAsync(_queue.QueueUrl, message.ReceiptHandle, token),
            logger, runtime.Cancellation);

        if (_queue.DeleteMessageBatchSize > 1)
        {
            _deleteBlock = new Block<Message[]>((batch, _) => deleteBatchAsync(batch));
            _deleteBatching = new BatchingChannel<Message>(_queue.DeleteMessageBatchTimeout, _deleteBlock,
                _queue.DeleteMessageBatchSize);
        }

        // GH-3236: the receive loop is now a shared BackgroundReceiveLoop — it owns the task, the
        // catch -> log -> exponential-backoff -> continue policy, the idle delay when a poll returns nothing, the
        // heartbeat, and safe teardown. The listener just provides one poll-and-process iteration and reports the
        // loop's health through IReportReceiveLoopHealth.
        _loop = new BackgroundReceiveLoop(_queue.Uri, logger, pollOnceAsync, runtime.Cancellation);
        _loop.Start();
    }

    /// <summary>
    /// Send one envelope, giving up rather than retrying when it is simply too big for the queue.
    /// </summary>
    /// <remarks>
    /// Both callers are <see cref="RetryBlock{T}" />s, which retry until they succeed. An oversized
    /// message can never succeed -- SQS rejects it with a permanent SenderFault -- so letting that
    /// exception through would spin the block forever on a send that is impossible, which is the very
    /// failure GH-3926 exists to remove.
    /// </remarks>
    private async Task sendOrDiscardAsync(AmazonSqsQueue queue, Envelope envelope, string operation)
    {
        try
        {
            await queue.SendMessageAsync(envelope, _logger);
        }
        catch (SqsMessageTooLargeException e)
        {
            _logger.LogError(e, "Discarding envelope {Id} on {Operation} to {Queue} - it is too large for SQS and no retry can change that",
                envelope.Id, operation, queue.Uri);
        }
    }

    private async Task<bool> pollOnceAsync(CancellationToken token)
    {
        var request = new ReceiveMessageRequest(_queue.QueueUrl);
        _queue.ConfigureRequest(request);

        var results = await _transport.Client!.ReceiveMessageAsync(request, token);

        if (results.Messages == null || !results.Messages.Any())
        {
            // No work — the loop applies its idle delay before polling again.
            return false;
        }

        var envelopes = new List<Envelope>(results.Messages.Count);
        foreach (var message in results.Messages)
        {
            try
            {
                // GH-3926: a fragmented message is N SQS messages carrying Wolverine's own framing.
                // Nothing is acked until the whole set is in hand -- the reassembler holds the partial
                // and never deletes from SQS, so a crash halfway through just makes the fragments
                // visible again rather than losing the message.
                if (SqsMessageFragments.TryReadHeader(message, out var header))
                {
                    if (_reassembler.TryAccept(message, header, out var body, out var fragments))
                    {
                        envelopes.Add(buildEnvelope(fragments, body));
                    }

                    continue;
                }

                envelopes.Add(buildEnvelope(message));
            }
            catch (Exception e)
            {
                if (_deadLetterQueue != null)
                {
                    try
                    {
                        await _transport.Client.SendMessageAsync(new SendMessageRequest(
                            _deadLetterQueue.QueueUrl,
                            message.Body));
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception,
                            "Error while trying to directly send a dead letter message {Id} from {Uri}",
                            message.MessageId, _queue.Uri);
                    }
                }

                _logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId, _queue.Uri);
            }
        }

        // ReSharper disable once CoVariantArrayConversion
        if (envelopes.Any())
        {
            await _receiver.ReceivedAsync(this, envelopes.ToArray());
        }

        return true;
    }

    public ReceiveLoopStatus ReceiveLoopStatus => _loop.ReceiveLoopStatus;

    public DateTimeOffset? LastReceiveLoopActivityAt => _loop.LastReceiveLoopActivityAt;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            return new ValueTask(CompleteAsync(e.SqsMessages));
        }

        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await _requeueBlock.PostAsync(e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync();
        await flushPendingDeletesAsync();
        _requeueBlock.Dispose();
        _deadLetterBlock?.Dispose();
        _singleDeleteBlock.Dispose();

        if (_deleteBatching != null)
        {
            await _deleteBatching.DisposeAsync();
        }

        if (_deleteBlock != null)
        {
            await _deleteBlock.DisposeAsync();
        }
    }

    /// <summary>
    /// Push any accumulated-but-unsent deletes at SQS. Anything still in flight simply reappears
    /// after its visibility timeout.
    /// </summary>
    private async Task flushPendingDeletesAsync()
    {
        if (_deleteBatching == null)
        {
            return;
        }

        try
        {
            _deleteBatching.TriggerBatch();
            _deleteBatching.Complete();
            await _deleteBatching.WaitForCompletionAsync().WaitAsync(_drainTimeout);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error flushing pending SQS deletes for {Uri}", _queue.Uri);
        }
    }

    public Uri Address => _queue.Uri;

    public async ValueTask StopAsync()
    {
        await _loop.StopAsync(_drainTimeout);

        // Don't leave completed messages sitting in the batch window while this listener is paused
        // -- they'd reappear at the visibility timeout and be handled twice.
        _deleteBatching?.TriggerBatch();
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await _requeueBlock.PostAsync(e);
            return true;
        }

        return false;
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
        return _deadLetterBlock!.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    private AmazonSqsEnvelope buildEnvelope(Message message)
    {
        return buildEnvelope([message], message.Body);
    }

    /// <summary>
    /// Build one envelope from the message(s) that carried it. A fragmented message hands over every
    /// SQS message in the set plus the reassembled body; everything else is a set of one.
    /// </summary>
    private AmazonSqsEnvelope buildEnvelope(Message[] messages, string body)
    {
        var envelope = new AmazonSqsEnvelope(messages);

        // SQS only returns MessageAttributes when they were explicitly requested, and
        // brokers/SDKs may hand back a null collection when a message carries none (as is
        // the case for MassTransit/NServiceBus messages that keep their metadata in the body).
        // Guarantee a non-null dictionary so ISqsEnvelopeMapper implementations can read freely.
        var attributes = messages[0].MessageAttributes ?? new Dictionary<string, MessageAttributeValue>();
        _mapper.ReadEnvelopeData(envelope, body, attributes);

        // CritterWatch#942 — the Body string (base64, UTF-16, ~2.7× the wire payload size) is fully
        // mapped into the envelope now, and every later use of SqsMessage — single delete, batched
        // delete, requeue-then-delete — reads only ReceiptHandle. The envelope pins SqsMessage for
        // its whole time in flight (which, for buffered endpoints feeding a batching pipeline, can
        // be a long, deep queue), so release the one big thing on it. The mapping-failure path above
        // (raw forward to the dead-letter queue) still has Body because it never reaches here.
        foreach (var message in messages)
        {
            message.Body = null;
        }

        return envelope;
    }

    /// <summary>
    /// Complete every SQS message that carried one envelope. A fragmented message is only really
    /// handled once all of its fragments are deleted; deleting some of them would leave the rest to
    /// reappear at the visibility timeout as an incomplete set that can never be reassembled.
    /// </summary>
    public Task CompleteAsync(Message[] sqsMessages)
    {
        if (sqsMessages.Length == 1)
        {
            return CompleteAsync(sqsMessages[0]);
        }

        return Task.WhenAll(sqsMessages.Select(CompleteAsync));
    }

    public Task CompleteAsync(Message sqsMessage)
    {
        if (_deleteBatching == null)
        {
            return _transport.Client!.DeleteMessageAsync(_queue.QueueUrl, sqsMessage.ReceiptHandle);
        }

        return _deleteBatching.PostAsync(sqsMessage).AsTask();
    }

    /// <summary>
    /// Delete up to <c>DeleteMessageBatchSize</c> messages in one request. DeleteMessageBatch is
    /// not transactional -- SQS can reject individual entries inside an otherwise successful
    /// response -- so each failed entry falls back to a retried single delete. A delete that never
    /// lands only means the message reappears after its visibility timeout, which the durable inbox
    /// deduplicates.
    /// </summary>
    private async Task deleteBatchAsync(Message[] batch)
    {
        var entries = new List<DeleteMessageBatchRequestEntry>(batch.Length);
        for (var i = 0; i < batch.Length; i++)
        {
            entries.Add(new DeleteMessageBatchRequestEntry(i.ToString(), batch[i].ReceiptHandle));
        }

        DeleteMessageBatchResponse response;
        try
        {
            response = await _transport.Client!.DeleteMessageBatchAsync(
                new DeleteMessageBatchRequest(_queue.QueueUrl, entries));
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error deleting a batch of {Count} messages from {Uri}; falling back to individual deletes",
                batch.Length, _queue.Uri);

            foreach (var message in batch)
            {
                await _singleDeleteBlock.PostAsync(message);
            }

            return;
        }

        if (response.Failed == null || response.Failed.Count == 0)
        {
            return;
        }

        foreach (var entry in response.Failed)
        {
            if (int.TryParse(entry.Id, out var index) && index >= 0 && index < batch.Length)
            {
                _logger.LogWarning(
                    "SQS batch delete from {Uri} failed for entry {Id}: {Code} - {Message} (SenderFault: {SenderFault}). Retrying as a single delete",
                    _queue.Uri, entry.Id, entry.Code, entry.Message, entry.SenderFault);

                await _singleDeleteBlock.PostAsync(batch[index]);
            }
            else
            {
                _logger.LogError(
                    "SQS batch delete from {Uri} reported a failed entry with unrecognized Id {Id}: {Code} - {Message}",
                    _queue.Uri, entry.Id, entry.Code, entry.Message);
            }
        }
    }
}
