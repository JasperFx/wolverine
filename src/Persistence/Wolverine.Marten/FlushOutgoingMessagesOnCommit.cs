using Marten;
using Marten.Services;
using Wolverine.Marten.Persistence.Operations;
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
                session.QueueSqlCommand($"update {_messageStore.IncomingFullName} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' where id = ?", _context.Envelope.Id);
            }
            else
            {
                var envelope = Envelope.ForPersistedHandled(_context.Envelope);
                session.QueueOperation(new StoreIncomingEnvelope(_messageStore.IncomingFullName, envelope));
            }
            
            _context.Envelope.Status = EnvelopeStatus.Handled;
        }
        
        return Task.CompletedTask;
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _context.FlushOutgoingMessagesAsync();
    }
}