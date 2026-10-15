namespace Wolverine.Persistence.Durability;

/// <summary>
/// This is necessary when the Wolverine system has "ancillary" message
/// stores that are in separate databases
/// </summary>
internal class DelegatingMessageInbox : IMessageInbox, IMessageInboxHeartbeat
{
    private readonly IMessageInbox _inner;
    private readonly MessageStoreCollection _stores;

    public DelegatingMessageInbox(IMessageInbox inner, MessageStoreCollection stores)
    {
        _inner = inner;
        _stores = stores;
    }

    /// <summary>
    /// Each envelope's row lives in the store it came from, so the heartbeat goes to each store with its own
    /// envelopes. A store without the capability is skipped.
    /// </summary>
    public async Task RestartStaleClockAsync(int ownerId, IReadOnlyList<Envelope> envelopes)
    {
        foreach (var group in envelopes.GroupBy(x => x.Store?.Inbox ?? _inner))
        {
            if (group.Key is IMessageInboxHeartbeat heartbeat)
            {
                await heartbeat.RestartStaleClockAsync(ownerId, group.ToArray()).ConfigureAwait(false);
            }
        }
    }

    public Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).RescheduleExistingEnvelopeForRetryAsync(envelope);
    }

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).ScheduleExecutionAsync(envelope);
    }

    public Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        return (envelope.Store?.Inbox ?? _inner).MoveToDeadLetterStorageAsync(envelope, exception);
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).IncrementIncomingEnvelopeAttemptsAsync(envelope);
    }

    public Task StoreIncomingAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).StoreIncomingAsync(envelope);
    }

    // Reached from a batch receipt and, since GH-4319, from the durable local queue's coalesced
    // publish as well -- which is why the grouping below is load-bearing rather than defensive.
    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        // Route by ancillary store exactly like the single-envelope overload above —
        // this overload used to send the whole batch to the main store, which put
        // ancillary-owned envelopes in the wrong inbox (and mark-as-handled, which DOES
        // route by store, then updated the other database, leaving the misplaced row
        // stuck as Incoming). Surfaced by the GH-3492 durable delivery coalescing;
        // mirrors MarkIncomingEnvelopeAsHandledAsync below (GH-3492).
        var groups = envelopes.GroupBy(x => x.Store).ToList();
        if (groups.Count == 1)
        {
            await (groups[0].Key?.Inbox ?? _inner).StoreIncomingAsync(envelopes);
            return;
        }

        foreach (var group in groups)
        {
            await (group.Key?.Inbox ?? _inner).StoreIncomingAsync(group.ToList());
        }
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).MarkIncomingEnvelopeAsHandledAsync(envelope);
    }

    public Task DeleteIncomingEnvelopeAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).DeleteIncomingEnvelopeAsync(envelope);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        // Going to purposely leave this naive and let the whole thing be retried
        var groups = envelopes.GroupBy(x => x.Store).ToList();
        if (groups.Count == 1)
        {
            await (groups[0].Key?.Inbox ?? _inner).MarkIncomingEnvelopeAsHandledAsync(envelopes);
            return;
        }
        
        foreach (var group in groups)
        {
            await (group.Key?.Inbox ?? _inner).MarkIncomingEnvelopeAsHandledAsync(group.ToList());
        }
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        var databases = await _stores.FindAllAsync();
        foreach (var database in databases)
        {
            await database.Inbox.ReleaseIncomingAsync(ownerId, receivedAt);
        }
    }

    public Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        return _inner.ExistsAsync(envelope, cancellation);
    }
}