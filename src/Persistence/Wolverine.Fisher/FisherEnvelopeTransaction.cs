using Fisher;
using Wolverine.Fisher.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.Sqlite;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Fisher;

internal class FisherEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly MessageContext _context;
    private readonly int _nodeId;

    public FisherEnvelopeTransaction(IDocumentSession session, MessageContext context)
    {
        _context = context;
        if (context.Storage is SqliteMessageStore store)
        {
            Store = store;
            _nodeId = store.Durability.AssignedNodeNumber;
        }
        else if (context.Storage is MultiTenantedMessageStore { Main: SqliteMessageStore s })
        {
            Store = s;
            _nodeId = s.Durability.AssignedNodeNumber;
        }
        else
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using SQLite + Fisher as the backing message persistence");
        }

        Session = session;
    }

    public SqliteMessageStore Store { get; }

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

        try
        {
            // Might need to reset!
            _context.MultiFlushMode = MultiFlushMode.AllowMultiples;
            var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
            await PersistIncomingAsync(copy);
            await Session.SaveChangesAsync(cancellation);

            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
