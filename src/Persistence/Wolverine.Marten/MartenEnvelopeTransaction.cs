using Marten;
using Npgsql;
using Wolverine.RDBMS;
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

        // R2: the Handled row goes into the handler's own transaction rather than being committed here, so it commits
        // or rolls back with the work it records. Committed up front, a handler that then failed left a record of
        // completion behind, and the broker's redelivery of a message that was never handled was thrown away as a
        // duplicate of itself.
        //
        // The transaction is opened now and the row written into it now, not queued for SaveChangesAsync: a
        // delivery the inbox already finished is refused by the primary key before the handler runs, which is what
        // Eager promises a handler with side effects outside its transaction, and a duplicate delivered alongside
        // the original waits on the original's uncommitted row instead of running the handler at the same time.
        // MessageContext owns the envelope's flags, and puts them back if this rolls back
        await Session.BeginTransactionAsync(cancellation);

        var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
        var builder = Store.ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(Store, builder, copy);

        // Npgsql runs a command in its connection's open transaction, which is the session's
        await using var command = builder.Compile();
        command.Connection = Session.Connection;

        try
        {
            await command.ExecuteNonQueryAsync(cancellation);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }

        // SaveChangesAsync skips a session with nothing queued, and would then leave the transaction holding the
        // Handled row uncommitted -- a handler that saves nothing still has to leave its record of completion
        Session.QueueSqlCommand("select 1");

        return true;
    }
}