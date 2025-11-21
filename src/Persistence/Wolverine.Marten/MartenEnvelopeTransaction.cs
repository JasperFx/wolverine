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
    private readonly int _nodeId;

    public MartenEnvelopeTransaction(IDocumentSession session, MessageContext context)
    {
        _context = context;
        if (context.Storage is PostgresqlMessageStore store)
        {
            Store = store;
            _nodeId = store.Durability.AssignedNodeNumber;
        }
        else if (context.Storage is MultiTenantedMessageStore { Main: PostgresqlMessageStore s })
        {
            Store = s;
            _nodeId = s.Durability.AssignedNodeNumber;
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
    
    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (envelope.WasPersistedInInbox) return true;

        try
        {
            // Might need to reset!
            _context.MultiFlushMode = MultiFlushMode.AllowMultiples;
            var copy = Envelope.ForPersistedHandled(envelope);
            await PersistIncomingAsync(copy);
            await Session.SaveChangesAsync(cancellation);
            
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch (Exception )
        {
            return false;
        }
    }
}