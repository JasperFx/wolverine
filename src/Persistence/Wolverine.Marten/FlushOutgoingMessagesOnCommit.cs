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
                // GH-2155: When using ancillary stores (e.g., [MartenStore]), the incoming
                // envelope was persisted in the main store by DurableReceiver. We should only
                // mark it as handled within this Marten session if the session's store is
                // the same store. Otherwise, let DurableReceiver handle it via the main store.
                if (_context.Envelope.Store == null && _messageStore.Role == MessageStoreRole.Ancillary)
                {
                    return Task.CompletedTask;
                }

                var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);
                session.QueueSqlCommand($"update {_messageStore.IncomingFullName} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = ? where id = ?", keepUntil, _context.Envelope.Id);
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