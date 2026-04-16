using Marten;
using Marten.Services;
using Wolverine.Marten.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Marten;

internal class FlushOutgoingMessagesOnCommit : DocumentSessionListenerBase
{
    private readonly MessageContext _context;
    private readonly PostgresqlMessageStore _messageStore;

    public FlushOutgoingMessagesOnCommit(MessageContext context, PostgresqlMessageStore messageStore)
    {
        _context = context;
        _messageStore = messageStore;
    }

    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        // No need to do anything for HTTP requests
        if (_context.Envelope == null)
        {
            return Task.CompletedTask;
        }

        // Mark as handled!
        if (_context.Envelope.Destination != null)
        {
            if (_context.Envelope.WasPersistedInInbox)
            {
                // Determine which incoming table to update. The envelope may have been
                // persisted in the ancillary store (if on a different database) or the
                // main store (default). We need to update the correct table.
                var incomingTableName = _messageStore.IncomingFullName;

                if (_messageStore.Role == MessageStoreRole.Ancillary)
                {
                    if (_context.Envelope.Store is PostgresqlMessageStore envelopeStore)
                    {
                        // Envelope was routed to a specific store (possibly this one)
                        incomingTableName = envelopeStore.IncomingFullName;
                    }
                    else if (_context.Envelope.Store == null)
                    {
                        // GH-2382: Envelope was persisted in the main store. If we're on the
                        // same database, we can still update it within this transaction.
                        if (_context.Runtime.Storage is PostgresqlMessageStore mainStore
                            && mainStore.Uri == _messageStore.Uri)
                        {
                            incomingTableName = mainStore.IncomingFullName;
                        }
                        else
                        {
                            // Different database — can't update cross-database in one transaction.
                            // Let DurableReceiver handle it via the main store.
                            return Task.CompletedTask;
                        }
                    }
                }

                var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);
                session.QueueSqlCommand($"update {incomingTableName} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = ? where id = ?", keepUntil, _context.Envelope.Id);
                _context.Envelope.Status = EnvelopeStatus.Handled;
            }

            // This was buggy in real usage.
            // else
            // {
            //     var envelope = Envelope.ForPersistedHandled(_context.Envelope, DateTimeOffset.UtcNow, _context.Runtime.Options.Durability);
            //     session.QueueOperation(new StoreIncomingEnvelope(_messageStore.IncomingFullName, envelope));
            // }


        }

        return Task.CompletedTask;
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _context.FlushOutgoingMessagesAsync();
    }
}