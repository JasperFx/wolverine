using Marten;
using Wolverine.Marten.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Marten;

internal class MartenEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly MessageContext _context;

    // GH-5016: owned by any node (0) under ServerlessWithOutbox, which has no node record to own them
    private readonly int _nodeId;

    public MartenEnvelopeTransaction(IDocumentSession session, MessageContext context)
    {
        _context = context;
        if (context.Storage is PostgresqlMessageStore store)
        {
            Store = store;
            _nodeId = store.Durability.OutgoingOwnerId;
        }
        else if (context.Storage is MultiTenantedMessageStore { Main: PostgresqlMessageStore s })
        {
            Store = s;
            _nodeId = s.Durability.OutgoingOwnerId;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using Postgresql + Marten as the backing message persistence");
        }

        Session = session;
    }

    public PostgresqlMessageStore Store { get; }

    public IDocumentSession Session { get; }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        Session.StoreOutgoing(Store, envelope, _nodeId);
        return Task.CompletedTask;
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes) Session.StoreOutgoing(Store, envelope, _nodeId);

        return Task.CompletedTask;
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        Session.StoreIncoming(Store, envelope);
        return Task.CompletedTask;
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
    }
    
    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation)
    {
        if (envelope.WasPersistedInInbox) return true;

        // R2: a delivery this inbox already finished is refused before the handler runs, which is what Eager
        // promises a handler with side effects outside its transaction
        if (await _context.Storage.Inbox.ExistsAsync(envelope, cancellation))
        {
            return false;
        }

        // ...but the Handled row itself goes into the handler's own session rather than being committed here, so it
        // commits or rolls back with the work it records. Committed up front, a handler that then failed left a
        // record of completion behind, and the broker's redelivery of a message that was never handled was thrown
        // away as a duplicate of itself. Two deliveries racing past the check above meet at the row's primary key
        // when they commit, and the loser's work rolls back with it. MessageContext owns the envelope's flags
        var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
        await PersistIncomingAsync(copy);

        return true;
    }
}