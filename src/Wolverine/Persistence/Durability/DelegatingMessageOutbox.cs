namespace Wolverine.Persistence.Durability;

internal class DelegatingMessageOutbox : IMessageOutbox
{
    private readonly IMessageOutbox _inner;
    private readonly MessageStoreCollection _stores;

    public DelegatingMessageOutbox(IMessageOutbox inner, MessageStoreCollection stores)
    {
        _inner = inner;
        _stores = stores;
    }

    public Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        return _inner.LoadOutgoingAsync(destination);
    }

    public Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        return (envelope.Store?.Outbox ?? _inner).StoreOutgoingAsync(envelope, ownerId);
    }

    public async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        // Going to purposely leave this naive and let the whole thing be retried
        var groups = envelopes.GroupBy(x => x.Store).ToList();
        if (groups.Count == 1)
        {
            await (groups[0].Key?.Outbox ?? _inner).DeleteOutgoingAsync(envelopes);
            return;
        }
        
        foreach (var group in groups)
        {
            await (group.Key?.Outbox ?? _inner).DeleteOutgoingAsync(group.ToArray());
        }
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
    {
        return (envelope.Store?.Outbox ?? _inner).DeleteOutgoingAsync(envelope);
    }
    
    public Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        return _inner.DiscardAndReassignOutgoingAsync(discards, reassigned, nodeId);
    }
}