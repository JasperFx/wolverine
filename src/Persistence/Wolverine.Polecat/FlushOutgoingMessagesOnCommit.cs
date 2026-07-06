using Microsoft.Data.SqlClient;
using Polecat;
using Wolverine.Polecat.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
/// Polecat IDocumentSessionListener that marks incoming envelopes as handled
/// before save, and flushes outgoing messages after commit.
/// </summary>
internal class FlushOutgoingMessagesOnCommit : IDocumentSessionListener
{
    private readonly MessageContext _context;
    private readonly SqlServerMessageStore _messageStore;

    // Only flip the in-memory Envelope.Status to Handled once the commit actually
    // succeeds (AfterCommitAsync). Setting it pre-commit left the flag stale on
    // rollback, so DurableReceiver's _markAsHandled optimization (which skips the
    // real UPDATE when Status == Handled) would strand the row as 'Incoming'.
    // Mirrors the Wolverine.Marten FlushOutgoingMessagesOnCommit fix.
    private bool _queuedHandledUpdate;

    public FlushOutgoingMessagesOnCommit(MessageContext context, SqlServerMessageStore messageStore)
    {
        _context = context;
        _messageStore = messageStore;
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
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
                if (_context.Envelope.Store == null && _messageStore.Role == MessageStoreRole.Ancillary)
                {
                    return Task.CompletedTask;
                }

                var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);
                // Use ITransactionParticipant to execute the SQL in the same transaction
                session.AddTransactionParticipant(new MarkIncomingAsHandledParticipant(
                    _messageStore.IncomingFullName, _context.Envelope.Id, keepUntil));

                // Defer the in-memory status flip to AfterCommitAsync — the UPDATE
                // above is only durable if this batch commits. See _queuedHandledUpdate.
                _queuedHandledUpdate = true;
            }
        }

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, CancellationToken token)
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

/// <summary>
/// Polecat ITransactionParticipant that flushes outgoing messages after commit.
/// Used by PolecatOutbox when listeners can't be added post-construction.
/// </summary>
internal class FlushOutgoingMessagesParticipant : ITransactionParticipant
{
    private readonly MessageContext _context;
    private readonly SqlServerMessageStore _messageStore;

    public FlushOutgoingMessagesParticipant(MessageContext context, SqlServerMessageStore messageStore)
    {
        _context = context;
        _messageStore = messageStore;
    }

    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        // Mark incoming as handled if needed
        if (_context.Envelope?.Destination != null && _context.Envelope.WasPersistedInInbox)
        {
            if (_context.Envelope.Store == null && _messageStore.Role == MessageStoreRole.Ancillary)
            {
                return;
            }

            var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                $"update {_messageStore.IncomingFullName} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = @id";
            cmd.Parameters.AddWithValue("@keepUntil", keepUntil);
            cmd.Parameters.AddWithValue("@id", _context.Envelope.Id);
            await cmd.ExecuteNonQueryAsync(token);

            // Deliberately do NOT flip _context.Envelope.Status to Handled here: this
            // runs inside BeforeCommitAsync, before the transaction commits, and
            // ITransactionParticipant exposes no after-commit hook to defer to. Setting
            // it pre-commit left a stale flag on rollback that made DurableReceiver skip
            // the real mark-handled UPDATE, stranding the row as 'Incoming'. The
            // (idempotent) UPDATE via DurableReceiver._markAsHandled covers the success
            // path instead.
        }
    }
}

internal class MarkIncomingAsHandledParticipant : ITransactionParticipant
{
    private readonly string _incomingFullName;
    private readonly Guid _envelopeId;
    private readonly DateTimeOffset _keepUntil;

    public MarkIncomingAsHandledParticipant(string incomingFullName, Guid envelopeId, DateTimeOffset keepUntil)
    {
        _incomingFullName = incomingFullName;
        _envelopeId = envelopeId;
        _keepUntil = keepUntil;
    }

    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            $"update {_incomingFullName} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = @id";
        cmd.Parameters.AddWithValue("@keepUntil", _keepUntil);
        cmd.Parameters.AddWithValue("@id", _envelopeId);
        await cmd.ExecuteNonQueryAsync(token);
    }
}
