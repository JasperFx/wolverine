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

    public FlushOutgoingMessagesOnCommit(MessageContext context, SqlServerMessageStore messageStore)
    {
        _context = context;
        _messageStore = messageStore;
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
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
                if (_context.Envelope.Store == null && _messageStore.Role == MessageStoreRole.Ancillary)
                {
                    return Task.CompletedTask;
                }

                var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);
                // Use ITransactionParticipant to execute the SQL in the same transaction
                session.AddTransactionParticipant(new MarkIncomingAsHandledParticipant(
                    _messageStore.IncomingFullName, _context.Envelope.Id, keepUntil));
                _context.Envelope.Status = EnvelopeStatus.Handled;
            }
        }

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, CancellationToken token)
    {
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
            _context.Envelope.Status = EnvelopeStatus.Handled;
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
