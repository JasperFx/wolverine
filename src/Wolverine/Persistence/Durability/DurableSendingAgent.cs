using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

internal class DurableSendingAgent : SendingAgent
{
    private readonly RetryBlock<Envelope[]> _deleteOutgoingMany;
    private readonly RetryBlock<Envelope> _deleteOutgoingOne;
    private readonly RetryBlock<OutgoingMessageBatch> _enqueueForRetry;
    private readonly ILogger _logger;
    private readonly IMessageOutbox _outbox;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    // GH-4319. Null when the batch sizes are 1. The store gates the send, so these coalescers are
    // strictly timer-free: a lone envelope is written immediately and batches only ever form behind a
    // flush that was already in flight.
    //
    // GH-5171. When the outbox fans out to several databases (a database per tenant, or ancillary
    // stores), each database gets its own lane, so one slow tenant database cannot hold back the sends
    // of every other tenant on this endpoint.
    private readonly IEnvelopeStoreCoalescer? _storeCoalescer;
    private readonly IEnvelopeStoreCoalescer? _deleteCoalescer;

    private IList<Envelope> _queued = new List<Envelope>();

    public DurableSendingAgent(ISender sender, DurabilitySettings settings, ILogger logger,
        IMessageTracker messageLogger,
        IMessageOutbox outbox, Endpoint endpoint) : this(sender, settings, logger, messageLogger, outbox, endpoint, null, null)
    {
    }

    public DurableSendingAgent(ISender sender, DurabilitySettings settings, ILogger logger,
        IMessageTracker messageLogger,
        IMessageOutbox outbox, Endpoint endpoint, IWolverineRuntime? runtime,
        SendingFailurePolicies? sendingFailurePolicies) : base(logger, messageLogger, sender, settings, endpoint, runtime, sendingFailurePolicies)
    {
        _logger = logger;

        _outbox = outbox;

        _deleteOutgoingOne =
            new RetryBlock<Envelope>((e, _) => _outbox.DeleteOutgoingAsync(e), logger, settings.Cancellation);

        _deleteOutgoingMany = new RetryBlock<Envelope[]>((envelopes, _) => _outbox.DeleteOutgoingAsync(envelopes),
            logger, settings.Cancellation);

        _enqueueForRetry = new RetryBlock<OutgoingMessageBatch>((batch, _) => enqueueForRetryAsync(batch), _logger,
            _settings.Cancellation);

        if (settings.StoreOutgoingBatchSize > 1)
        {
            var partitionFor = EnvelopeStoreCoalescing.PartitionerFor(_outbox);

            _storeCoalescer = EnvelopeStoreCoalescing.Build(partitionFor,
                envelopes => _outbox.StoreOutgoingAsync(envelopes, settings.OutgoingOwnerId),
                envelope => _outbox.StoreOutgoingAsync(envelope, settings.OutgoingOwnerId),
                settings.StoreOutgoingBatchSize, endpoint.Uri, logger);

            // GH-4319. The success path already had a many-envelope DELETE -- OutgoingMessageBatch has
            // used it since it existed -- but a single-envelope send paid its own round trip to remove
            // one row. Same coalescer, same guarantee: MarkSuccessfulAsync does not return until this
            // envelope's row is gone, which is what makes releasing a pooled envelope afterwards safe.
            _deleteCoalescer = EnvelopeStoreCoalescing.Build(partitionFor,
                envelopes => _outbox.DeleteOutgoingAsync(envelopes as Envelope[] ?? envelopes.ToArray()),
                envelope => _deleteOutgoingOne.PostAsync(envelope),
                settings.StoreOutgoingBatchSize, endpoint.Uri, logger);
        }

    }

    public override bool IsDurable => true;

    protected override IMessageOutbox? resolveOutbox() => _outbox;

    protected override async Task drainOtherAsync()
    {
        if (_storeCoalescer != null)
        {
            await _storeCoalescer.DrainAsync();
        }

        if (_deleteCoalescer != null)
        {
            await _deleteCoalescer.DrainAsync();
        }

        await _deleteOutgoingMany.DrainAsync();
        await _deleteOutgoingOne.DrainAsync();
        await _enqueueForRetry.DrainAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _deleteOutgoingMany.Dispose();
        _deleteOutgoingOne.Dispose();
        _enqueueForRetry.Dispose();
        _queueLock.Dispose();
    }

    public override Task EnqueueForRetryAsync(OutgoingMessageBatch batch)
    {
        return _enqueueForRetry.PostAsync(batch);
    }

    private async Task enqueueForRetryAsync(OutgoingMessageBatch batch)
    {
        if (_settings.Cancellation.IsCancellationRequested)
        {
            return;
        }

        await _queueLock.WaitAsync();
        try
        {
            var (expiredInQueue, notExpiredInQueue) = SplitByExpiration(_queued);
            var (expiredInBatch, notExpiredInBatch) = SplitByExpiration(batch.Messages);

            var expired = new Envelope[expiredInBatch.Length + expiredInQueue.Length];
            expiredInBatch.CopyTo(expired, 0);
            expiredInQueue.CopyTo(expired, expiredInBatch.Length);

            var all = new List<Envelope>(notExpiredInBatch.Length + notExpiredInQueue.Length);
            all.AddRange(notExpiredInBatch);
            all.AddRange(notExpiredInQueue);

            var (retained, reassigned) = TrimToMaxCapacity(all, Endpoint.MaximumEnvelopeRetryStorage);

            await executeWithRetriesAsync(async () =>
            {
                await _outbox.DiscardAndReassignOutgoingAsync(expired, reassigned, TransportConstants.AnyNode);
                _logger.DiscardedExpired(expired);
            });

            _queued = retained;
        }
        finally
        {
            _queueLock.Release();
        }

        static (Envelope[] expired, Envelope[] notExpired) SplitByExpiration(IEnumerable<Envelope> messages)
        {
            var expiredList = new List<Envelope>();
            var notExpiredList = new List<Envelope>();
            foreach (var msg in messages)
            {
                if (msg.IsExpired())
                    expiredList.Add(msg);
                else
                    notExpiredList.Add(msg);
            }

            return (expiredList.ToArray(), notExpiredList.ToArray());
        }

        static (List<Envelope> retained, Envelope[] reassigned) TrimToMaxCapacity(
            List<Envelope> messages, int maxCapacity)
        {
            if (messages.Count <= maxCapacity)
                return (messages, Array.Empty<Envelope>());

            var reassigned = new Envelope[messages.Count - maxCapacity];
            for (var i = 0; i < reassigned.Length; i++)
            {
                reassigned[i] = messages[maxCapacity + i];
            }

            messages.RemoveRange(maxCapacity, reassigned.Length);
            return (messages, reassigned);
        }
    }

    protected override async Task afterRestartingAsync(ISender sender)
    {
        Envelope[] toRetry;
        await _queueLock.WaitAsync();
        try
        {
            var expiredList = new List<Envelope>();
            var retryList = new List<Envelope>();
            foreach (var msg in _queued)
            {
                if (msg.IsExpired())
                    expiredList.Add(msg);
                else
                    retryList.Add(msg);
            }

            var expired = expiredList.ToArray();
            toRetry = retryList.ToArray();

            if (expired.Length != 0)
            {
                await executeWithRetriesAsync(() => _outbox.DeleteOutgoingAsync(expired));
            }

            _queued.Clear();
        }
        finally
        {
            _queueLock.Release();
        }

        foreach (var envelope in toRetry) await _sending.PostAsync(envelope);
    }

    public override Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        return _deleteOutgoingMany.PostAsync(outgoing.Messages.ToArray());
    }

    /// <summary>
    ///     GH-3926: an envelope the transport has judged permanently unsendable has to leave the outgoing
    ///     table as well as the in-memory queue. Logging it and stopping there leaves the row behind, and
    ///     the durability agent re-reads and re-sends it on every recovery sweep -- which is precisely the
    ///     endless retry this path exists to break. GH-5055: it is moved to dead letter storage first, and
    ///     a row whose dead letter could not be written stays in the outbox for a later recovery sweep, so
    ///     nothing is lost
    /// </summary>
    public override async Task MarkSerializationFailureAsync(OutgoingMessageBatch outgoing)
    {
        var notMoved = await MoveUnsendableToDeadLetterStorageAsync(outgoing);

        var toDelete = notMoved.Count == 0
            ? outgoing.Messages.ToArray()
            : outgoing.Messages.Where(x => !notMoved.Contains(x)).ToArray();

        if (toDelete.Length > 0)
        {
            await _deleteOutgoingMany.PostAsync(toDelete);
        }
    }

    public override Task MarkSuccessfulAsync(Envelope outgoing)
    {
        if (_deleteCoalescer != null)
        {
            return _deleteCoalescer.StoreAsync(outgoing);
        }

        return _deleteOutgoingOne.PostAsync(outgoing);
    }

    protected override async Task storeAndForwardAsync(Envelope envelope)
    {
        using var activity = Endpoint.TelemetryEnabled ? WolverineTracing.StartSending(envelope) : null;

        await storeOutgoingAsync(envelope);

        // GH-5016: a send with no outbox transaction behind it, like a Wolverine.HTTP endpoint without
        // transactional middleware. Nothing can be trusted to run after a serverless request completes, so
        // send it right now instead of handing it to the background sending block
        if (_settings.Mode == DurabilityMode.ServerlessWithOutbox && !Latched &&
            !envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            await sendInlineAsync(envelope);
        }
        else
        {
            await _sending.PostAsync(envelope);
        }

        activity?.Stop();
    }

    // The outbox row was written first, so a failed send -- or a function frozen mid-send -- leaves it for the
    // next durability pass to recover
    private async Task sendInlineAsync(Envelope envelope)
    {
        // The grouped send never throws, and tells a send that failed apart from one that never can succeed
        var result = await _sender.SendToBrokerAsync([envelope]);

        if (!result.WasAccepted(envelope))
        {
            // GH-5055. The transport can never send it, like an envelope its mapper throws on, so it goes to dead
            // letter storage now rather than failing again in every later durability pass
            if (result.IsSerializationFailure(envelope, out _))
            {
                var notMoved = await MoveUnsendableToDeadLetterStorageAsync(
                    new OutgoingMessageBatch(envelope.Destination ?? Destination, [envelope]));
                if (notMoved.Count == 0)
                {
                    await deleteInlineSentAsync(envelope);
                }

                return;
            }

            _logger.LogError(result.Exception,
                "Unable to send outgoing message {Envelope} inline to {Destination}. It is left in the outbox to be recovered by a later durability pass",
                envelope, envelope.Destination);
            return;
        }

        await deleteInlineSentAsync(envelope);
    }

    private async Task deleteInlineSentAsync(Envelope envelope)
    {
        try
        {
            await _outbox.DeleteOutgoingAsync(envelope);
        }
        catch (Exception e)
        {
            // The message was sent (or dead lettered), so the worst case is a duplicate send by a later durability pass
            _logger.LogError(e,
                "Unable to delete the outbox row of message {Envelope} that was already sent inline. A later durability pass may send it again",
                envelope);
        }
    }

    /// <summary>
    ///     GH-4824. The store half of <see cref="storeAndForwardAsync" />, on its own, so a caller that
    ///     has to settle other durable state between the store and the send can do that. The outbox row is
    ///     what makes the envelope recoverable, and this hands it back the moment that row exists.
    /// </summary>
    public override async ValueTask<bool> TryStoreOutgoingAsync(Envelope envelope)
    {
        // The same defaults StoreAndForwardAsync applies before storing -- Status, OwnerId, ReplyUri --
        // because they are part of the row that gets written. Idempotent, so the second application
        // inside EnqueueOutgoingAsync changes nothing.
        setDefaults(envelope);

        await storeOutgoingAsync(envelope);

        return true;
    }

    // GH-4662: the outbox row is this envelope's only durable home, so its write is retried inline
    // and, on exhaustion, thrown to whoever called PublishAsync/SendAsync. A RetryBlock handed the
    // caller a completed task after the first failed attempt and then discarded the envelope with an
    // Information line -- the message was neither persisted nor sent, and the caller was told it was
    // sent. SendingAgent.StoreAndForwardAsync only logs Sent after this returns, so a throw here
    // makes its "a store that throws still reports no send" comment true.
    private Task storeOutgoingAsync(Envelope envelope)
    {
        return DurableWriteRetry.ExecuteAsync(() => serializeAndStoreOutgoingAsync(envelope),
            envelope, _logger, _settings.Cancellation);
    }

    // The store reads Envelope.Data synchronously, so an async serializer finishes first. Inside the retried
    // write, so a transient serializer failure (an unreachable schema registry) gets the same retries as before
    private Task serializeAndStoreOutgoingAsync(Envelope envelope)
    {
        var serializing = envelope.SerializeAheadAsync();
        if (!serializing.IsCompletedSuccessfully)
        {
            return serializeThenStoreOutgoingAsync(envelope, serializing);
        }

        return writeOutgoingRowAsync(envelope);
    }

    private async Task serializeThenStoreOutgoingAsync(Envelope envelope, ValueTask serializing)
    {
        await serializing.ConfigureAwait(false);
        await writeOutgoingRowAsync(envelope).ConfigureAwait(false);
    }

    private Task writeOutgoingRowAsync(Envelope envelope)
    {
        return _storeCoalescer != null
            ? _storeCoalescer.StoreAsync(envelope)
            : _outbox.StoreOutgoingAsync(envelope, _settings.OutgoingOwnerId);
    }
}