namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5962. Collects one handler's durable outgoing envelopes into inbox and outbox rows, split by the message
///     store each one belongs in, so <see cref="IMessageStore.StoreEnvelopesAsync" /> can write each store's share
///     at once. As an <see cref="IEnvelopeTransaction" /> it goes through the same
///     <see cref="EnvelopeTransactionExtensions.PersistAsync(IEnvelopeTransaction, Envelope)" /> routing as every
///     other outbox, so a local queue's envelope becomes an inbox row and a broker's an outbox row exactly as they
///     would in a Marten session or a DbContext.
/// </summary>
internal sealed class OutgoingEnvelopeBatch : IEnvelopeTransaction
{
    private readonly IMessageStore _defaultStore;

    public OutgoingEnvelopeBatch(IMessageStore defaultStore)
    {
        _defaultStore = defaultStore;
    }

    public Dictionary<IMessageStore, (List<Envelope> Incoming, List<Envelope> Outgoing)> Stores { get; } = new();

    public bool IsEmpty => Stores.Count == 0;

    public IEnumerable<Envelope> AllEnvelopes()
    {
        foreach (var (incoming, outgoing) in Stores.Values)
        {
            foreach (var envelope in incoming) yield return envelope;
            foreach (var envelope in outgoing) yield return envelope;
        }
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        groupFor(envelope).Outgoing.Add(envelope);
        return Task.CompletedTask;
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes) groupFor(envelope).Outgoing.Add(envelope);
        return Task.CompletedTask;
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        groupFor(envelope).Incoming.Add(envelope);
        return Task.CompletedTask;
    }

    public ValueTask RollbackAsync()
    {
        Stores.Clear();
        return ValueTask.CompletedTask;
    }

    public Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation)
    {
        throw new NotSupportedException();
    }

    private (List<Envelope> Incoming, List<Envelope> Outgoing) groupFor(Envelope envelope)
    {
        var store = envelope.Store ?? _defaultStore;
        if (!Stores.TryGetValue(store, out var group))
        {
            group = ([], []);
            Stores[store] = group;
        }

        return group;
    }
}
