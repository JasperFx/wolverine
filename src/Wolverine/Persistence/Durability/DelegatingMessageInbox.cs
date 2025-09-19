namespace Wolverine.Persistence.Durability;

/// <summary>
/// This is necessary when the Wolverine system has "ancillary" message
/// stores that are in separate databases
/// </summary>
internal class DelegatingMessageInbox : IMessageInbox
{
    private readonly IMessageInbox _inner;
    private readonly MessageStoreCollection _stores;

    public DelegatingMessageInbox(IMessageInbox inner, MessageStoreCollection stores)
    {
        _inner = inner;
        _stores = stores;
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

    // This would only be coming from a batch receipt and not from any kind of 
    // local queueing
    public Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        return _inner.StoreIncomingAsync(envelopes);
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return (envelope.Store?.Inbox ?? _inner).MarkIncomingEnvelopeAsHandledAsync(envelope);
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
}