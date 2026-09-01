using Amazon.SQS.Model;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener, ISupportDeadLetterQueue, IReportReceiveLoopHealth, ISupportLeaseRenewal
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

    // GH-4019: inline listeners only. Keeps a received batch invisible while its handlers run, because
    // the inline receiver works through the batch one message at a time and the visibility timeout
    // was only ever set once, on the receive. Null unless the endpoint opts in.
    private readonly SqsVisibilityHeartbeat? _heartbeat;

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

        // GH-4012 items 3 and 5: a delete that can never succeed must not burn the retry budget, and the
        // classification lives on the block rather than in a catch inside the callback.
        _singleDeleteBlock = new RetryBlock<Message>(
            (message, token) => SqsSettlement.DeleteAsync(_transport.Client!, _queue.QueueUrl!,
                message.ReceiptHandle, token),
            logger, runtime.Cancellation)
        {
            ShouldRetry = e => !SqsSettlement.IsTerminal(e),
            OnTerminalFailure = (_, e) =>
            {
                SqsSettlement.LogTerminalDelete(logger, _queue.QueueUrl!, e);
                return Task.CompletedTask;
            }
        };

        if (_queue.DeleteMessageBatchSize > 1)
        {
            _deleteBlock = new Block<Message[]>((batch, _) => deleteBatchAsync(batch));
            _deleteBatching = new BatchingChannel<Message>(_queue.DeleteMessageBatchTimeout, _deleteBlock,
                _queue.DeleteMessageBatchSize);
        }

        if (ShouldExtendVisibility(_queue))
        {
            _heartbeat = new SqsVisibilityHeartbeat(TimeSpan.FromSeconds(_queue.VisibilityTimeout),
                _queue.MaximumVisibilityExtension, extendVisibilityAsync, _queue.Uri, logger, runtime.Cancellation);
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
            if (_heartbeat == null)
            {
                await _receiver.ReceivedAsync(this, envelopes.ToArray());
            }
            else
            {
                // Only the messages that became envelopes. Fragments still waiting on their siblings in
                // the reassembler are meant to reappear at the visibility timeout if the set never completes.
                var inFlight = envelopes.OfType<AmazonSqsEnvelope>().SelectMany(x => x.SqsMessages).ToArray();
                _heartbeat.Track(inFlight);
                try
                {
                    await _receiver.ReceivedAsync(this, envelopes.ToArray());
                }
                finally
                {
                    // The inline receiver has run every handler by now. Anything still unsettled is in
                    // a requeue or dead-letter retry block that deletes it; stop holding it invisible.
                    _heartbeat.Untrack(inFlight);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// GH-4019: this heartbeat covers an <em>inline</em> listener, which works through a received batch one
    /// message at a time against a visibility timeout that was only set once, on the receive. Durable deletes
    /// the message right after the inbox insert and Buffered deletes on receipt, so neither holds a message
    /// under the visibility timeout while a handler runs.
    /// </summary>
    /// <remarks>
    /// GH-4048 corrected the half of the old comment that said no other mode holds a message under the
    /// visibility timeout. <c>NativeAck</c> holds it for lane queue time <em>plus</em> handler time -- longer
    /// than Inline ever does, and unbounded by design. That mode is renewed <b>unconditionally</b>, without
    /// consulting <see cref="AmazonSqsQueue.ExtendVisibilityWhileHandling" />, but the renewal is driven from
    /// core through <see cref="ISupportLeaseRenewal" /> rather than from this heartbeat: the heartbeat's
    /// Track/Untrack pair straddles <c>IReceiver.ReceivedAsync</c>, which under NativeAck returns as soon as
    /// the envelope is enqueued -- i.e. at the very moment the risk window opens. A second tick loop here would
    /// therefore renew nothing. Both loops share <see cref="extendVisibilityAsync" />, which is the whole of
    /// the SQS-side work either one does.
    /// </remarks>
    internal static bool ShouldExtendVisibility(AmazonSqsQueue queue)
    {
        return queue.ExtendVisibilityWhileHandling && queue.Mode == EndpointMode.Inline;
    }

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(_queue.VisibilityTimeout);

    public TimeSpan MaximumLeaseExtension => _queue.MaximumVisibilityExtension;

    /// <summary>
    /// GH-4048. Keep these queued-but-unsettled deliveries invisible. Deliberately does NOT consult
    /// <see cref="AmazonSqsQueue.ExtendVisibilityWhileHandling" />: that flag is a cost trade for Inline, where
    /// exposure is bounded by how long one receive of at most ten messages takes to run. Under NativeAck the
    /// exposure is lane depth, which the mode's back-pressure model deliberately allows to grow, so renewal is
    /// mandatory. The same reasoning made fragment reassembly unconditional in this listener.
    /// </summary>
    public async ValueTask<IReadOnlyList<Envelope>> RenewLeasesAsync(IReadOnlyList<Envelope> envelopes,
        CancellationToken token)
    {
        // One envelope can be several SQS messages after GH-3926 fragment reassembly, and the whole set has to
        // stay invisible together -- an incomplete set that reappears can never be reassembled.
        var owners = new Dictionary<string, Envelope>();
        var messages = new List<Message>(envelopes.Count);

        foreach (var envelope in envelopes)
        {
            if (envelope is not AmazonSqsEnvelope sqs) continue;

            foreach (var message in sqs.SqsMessages)
            {
                if (message.ReceiptHandle == null) continue;
                if (owners.TryAdd(message.ReceiptHandle, envelope))
                {
                    messages.Add(message);
                }
            }
        }

        if (messages.Count == 0)
        {
            return [];
        }

        var dropped = await extendVisibilityAsync(messages.ToArray(), token);
        if (dropped.Count == 0)
        {
            return [];
        }

        // Any fragment SQS would not extend loses the lease for the whole envelope.
        var lost = new List<Envelope>();
        foreach (var message in dropped)
        {
            if (message.ReceiptHandle == null) continue;
            if (owners.TryGetValue(message.ReceiptHandle, out var envelope) && !lost.Contains(envelope))
            {
                lost.Add(envelope);
            }
        }

        return lost;
    }

    /// <summary>
    /// GH-4019: re-arm the visibility timeout on these in-flight messages. Returns the ones SQS would
    /// not extend -- a stale receipt handle means the message was already deleted or redelivered, and
    /// there is nothing left to keep alive.
    /// </summary>
    private async Task<IReadOnlyList<Message>> extendVisibilityAsync(Message[] messages, CancellationToken token)
    {
        var dropped = new List<Message>();

        foreach (var chunk in messages.Chunk(AmazonSqsQueue.MaximumDeleteBatchSize))
        {
            var entries = new List<ChangeMessageVisibilityBatchRequestEntry>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                entries.Add(new ChangeMessageVisibilityBatchRequestEntry(i.ToString(), chunk[i].ReceiptHandle)
                {
                    VisibilityTimeout = _queue.VisibilityTimeout
                });
            }

            var response = await _transport.Client!.ChangeMessageVisibilityBatchAsync(_queue.QueueUrl, entries, token);
            if (response.Failed == null || response.Failed.Count == 0)
            {
                continue;
            }

            foreach (var entry in response.Failed)
            {
                if (int.TryParse(entry.Id, out var index) && index >= 0 && index < chunk.Length)
                {
                    _logger.LogDebug(
                        "SQS would not extend the visibility of message {MessageId} at {Uri}: {Code} - {Message}. No longer keeping it invisible",
                        chunk[index].MessageId, _queue.Uri, entry.Code, entry.Message);
                    dropped.Add(chunk[index]);
                }
            }
        }

        return dropped;
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
        if (_heartbeat != null)
        {
            await _heartbeat.DisposeAsync();
        }

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
    /// GH-4012 item 4. SQS's own delivery count, requested as a system attribute in
    /// <c>AmazonSqsQueue.ConfigureRequest</c>. Null when absent -- an older queue, a custom receive, or a
    /// broker emulator that does not populate it -- which leaves the redelivery bound simply inactive
    /// rather than guessing.
    /// </summary>
    private static int? readApproximateReceiveCount(Message message)
    {
        if (message.Attributes == null) return null;

        return message.Attributes.TryGetValue("ApproximateReceiveCount", out var raw)
               && int.TryParse(raw, out var count)
            ? count
            : null;
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

        // GH-4012 item 4. Read AFTER the mapper, so a custom ISqsEnvelopeMapper cannot accidentally clear
        // it -- this is Wolverine's own bookkeeping rather than user-mapped data. A fragmented message is
        // one logical delivery, so the first fragment's count speaks for the set.
        envelope.BrokerDeliveryCount = readApproximateReceiveCount(messages[0]);

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
        _heartbeat?.Settled(sqsMessage);

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
