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

    // Tracks whether BeforeSaveChangesAsync queued the "mark incoming handled"
    // UPDATE into this batch. The in-memory Envelope.Status flag is only flipped
    // once the commit actually succeeds (in AfterCommitAsync). Flipping it before
    // the commit left it stale on rollback (e.g. a duplicate document insert), so
    // DurableReceiver's _markAsHandled optimization — which skips the real UPDATE
    // when Status == Handled — would strand the row as 'Incoming' forever and it
    // would be reprocessed on every reclaim/restart. See
    // Bug_discard_after_failed_outbox_commit.
    private bool _queuedHandledUpdate;

    public FlushOutgoingMessagesOnCommit(MessageContext context, PostgresqlMessageStore messageStore)
    {
        _context = context;
        _messageStore = messageStore;
    }

    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        _queuedHandledUpdate = false;

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
                        // Envelope was routed to a specific store. Only fold the
                        // handled-update into THIS Marten transaction if envelopeStore
                        // sits on the same connection / schema as _messageStore — the
                        // session is open against _messageStore's database, so an
                        // UPDATE against a different database's inbox table simply
                        // can't run here. Compare by Uri (the existing same-database
                        // heuristic in the envelope.Store==null branch below uses
                        // the same approach), which keeps this from depending on
                        // IMessageStore.Id and matches the local notion of "same
                        // store" the rest of this method already uses.
                        //
                        // Cross-store envelopes (e.g. a main-store handler dispatches
                        // a local message to an ancillary-store handler — GH-2669)
                        // are skipped here so the envelope's owning store handles
                        // the mark-handled separately via its own connection.
                        if (envelopeStore.Uri == _messageStore.Uri)
                        {
                            incomingTableName = envelopeStore.IncomingFullName;
                        }
                        else
                        {
                            return Task.CompletedTask;
                        }
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

                // Defer the in-memory status flip to AfterCommitAsync — the UPDATE
                // above is only durable if this batch commits. See _queuedHandledUpdate.
                _queuedHandledUpdate = true;
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
        // The queued mark-handled UPDATE is only durable now that the commit
        // succeeded, so it's safe to trust the in-memory optimization flag.
        if (_queuedHandledUpdate && _context.Envelope != null)
        {
            _context.Envelope.Status = EnvelopeStatus.Handled;
        }

        return _context.FlushOutgoingMessagesAsync();
    }
}