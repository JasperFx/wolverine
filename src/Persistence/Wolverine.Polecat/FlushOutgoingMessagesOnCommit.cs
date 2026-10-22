using Microsoft.Data.SqlClient;
using Polecat;
using Polecat.Services;
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
            // F13: a handler that rescheduled its envelope handed the row back as Scheduled; marking it here lost the run
            if (_context.Envelope.WasPersistedInInbox && !_context.HasRescheduledEnvelope)
            {
                // GH-5045: only when THIS store's database holds the row. Checking for a null Envelope.Store let
                // an envelope persisted in the main store (a handler registered with AddMessageHandler is not
                // in the ancillary map) be marked in the ancillary database, matching no rows, and the Handled
                // status flipped in AfterCommitAsync then made DurableReceiver skip the mark that would have
                // found it. Leave it to DurableReceiver, which marks it in the envelope's own store
                if (!_context.Envelope.InboxRowIsIn(_messageStore, _context.Runtime.Storage))
                {
                    return Task.CompletedTask;
                }

                var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);
                // Use ITransactionParticipant to execute the SQL in the same transaction
                session.AddTransactionParticipant(new MarkIncomingAsHandledParticipant(
                    _messageStore, _context.Envelope, keepUntil));

                // Defer the in-memory status flip to AfterCommitAsync — the UPDATE
                // above is only durable if this batch commits. See _queuedHandledUpdate.
                _queuedHandledUpdate = true;
            }
        }

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        // The queued mark-handled UPDATE is only durable now that the commit
        // succeeded, so it's safe to trust the in-memory optimization flag.
        if (_queuedHandledUpdate && _context.Envelope != null)
        {
            _context.Envelope.Status = EnvelopeStatus.Handled;
        }

        // GH-5016: nothing can be trusted to run after a serverless request completes, so the messages are
        // sent right now and their outbox rows deleted in one batch. A failed send stays in the outbox
        // for the next durability pass
        if (_context.Runtime.Options.Durability.Mode == DurabilityMode.ServerlessWithOutbox)
        {
            return _context.SendAllOutgoingMessagesInlineAsync();
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
        // F13: see FlushOutgoingMessagesOnCommit
        if (_context.Envelope?.Destination != null && _context.Envelope.WasPersistedInInbox && !_context.HasRescheduledEnvelope)
        {
            // GH-5045: see FlushOutgoingMessagesOnCommit
            if (!_context.Envelope.InboxRowIsIn(_messageStore, _context.Runtime.Storage))
            {
                return;
            }

            var keepUntil = DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling);

            // GH-4705: same statement the store runs itself, so it matches id AND received_at.
            var markHandled =
                _messageStore.BuildMarkIncomingAsHandled(_context.Envelope, keepUntil, "@id", "@uri", "@keepUntil");

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = markHandled.Sql;
            cmd.Parameters.AddWithValue("@keepUntil", keepUntil);
            cmd.Parameters.AddWithValue("@id", _context.Envelope.Id);
            cmd.Parameters.AddWithValue("@uri", _context.Envelope.Destination!.ToString());
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
    private readonly SqlServerMessageStore _messageStore;
    private readonly Envelope _envelope;
    private readonly DateTimeOffset _keepUntil;

    public MarkIncomingAsHandledParticipant(SqlServerMessageStore messageStore, Envelope envelope, DateTimeOffset keepUntil)
    {
        _messageStore = messageStore;
        _envelope = envelope;
        _keepUntil = keepUntil;
    }

    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        // GH-4705: the statement comes from the message store rather than being written here, so it matches
        // the whole inbox identity -- received_at as well as id -- and cannot drift from the one the store
        // runs itself. Binding is by name, so the Arguments the store hands back are not needed.
        var markHandled = _messageStore.BuildMarkIncomingAsHandled(_envelope, _keepUntil, "@id", "@uri", "@keepUntil");

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = markHandled.Sql;
        cmd.Parameters.AddWithValue("@keepUntil", _keepUntil);
        cmd.Parameters.AddWithValue("@id", _envelope.Id);
        cmd.Parameters.AddWithValue("@uri", _envelope.Destination!.ToString());
        await cmd.ExecuteNonQueryAsync(token);
    }
}
