using System.Data.Common;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.Outbox;

/// <summary>
/// GH-5144. What a document store session enlisted in Wolverine's outbox does around its commit, written once for
/// Marten, Polecat and Fisher: decide whether the incoming envelope's inbox row is marked handled in the session's
/// own transaction, run that statement over the store's <see cref="DbConnection" />, and after the commit flip the
/// envelope to Handled and release the outgoing messages.
/// </summary>
/// <remarks>
/// <para>
/// One of these is made per session. Each store keeps only the adapter its driver needs: a session listener for the
/// before-save and after-commit points, and a transaction participant over its own connection type. Those adapters
/// forward here, so the rules below cannot drift between the three again.
/// </para>
/// <para>
/// ⚠️ The in-memory <see cref="Envelope.Status" /> flips to <see cref="EnvelopeStatus.Handled" /> only in
/// <see cref="AfterCommitAsync" />. Flipped before the commit, a rolled-back transaction left it stale, and
/// <c>DurableReceiver</c>, which skips its own mark when the status already says Handled, stranded the row as
/// Incoming (Bug_discard_after_failed_outbox_commit).
/// </para>
/// </remarks>
internal sealed class DocumentSessionOutboxCommit
{
    private readonly MessageContext _context;
    private readonly IMessageDatabase _messageStore;
    private bool _queuedHandledUpdate;

    public DocumentSessionOutboxCommit(MessageContext context, IMessageDatabase messageStore)
    {
        _context = context;
        _messageStore = messageStore;
    }

    public MessageContext Context => _context;

    /// <summary>
    /// Call once at the start of each save. Answers the inbox mark this transaction should carry, or null when it
    /// should carry none, and remembers which so <see cref="AfterCommitAsync" /> knows whether to trust it.
    /// </summary>
    public PendingInboxMark? StartSave()
    {
        _queuedHandledUpdate = false;

        var envelope = _context.Envelope;

        // No envelope is an HTTP request, and no destination is nothing a listener received
        if (envelope?.Destination == null) return null;

        if (!envelope.WasPersistedInInbox) return null;

        // F13: a handler that rescheduled its envelope handed the row back as Scheduled; marking it here lost the run
        if (_context.HasRescheduledEnvelope) return null;

        // GH-5045: only when THIS store's database holds the row. Marked in another database it matches no rows,
        // and the Handled status flipped after the commit then makes DurableReceiver skip the mark that would have
        // found it. Leave it to DurableReceiver, which marks it in the envelope's own store
        if (!envelope.InboxRowIsIn(_messageStore, _context.Runtime.Storage)) return null;

        var keepUntil = MarkAsHandledCommand.KeepUntilForMark(
            DateTimeOffset.UtcNow.Add(_context.Runtime.Options.Durability.KeepAfterMessageHandling));

        _queuedHandledUpdate = true;
        return new PendingInboxMark(statementStoreFor(envelope), envelope, keepUntil,
            !_context.EagerIdempotencyRowWritten);
    }

    /// <summary>
    /// The store whose statement is written. The row is in a database with the session's own Uri, but when the
    /// envelope's owner is a different store object on that database, it is that store's table name that holds it
    /// (GH-2382, GH-2669).
    /// </summary>
    private IMessageDatabase statementStoreFor(Envelope envelope)
    {
        if (_messageStore.Role != MessageStoreRole.Ancillary) return _messageStore;

        var owner = envelope.Store ?? _context.Runtime.Storage;
        return owner is IMessageDatabase database && database.Uri == _messageStore.Uri
            ? database
            : _messageStore;
    }

    /// <summary>
    /// Call after the commit, never from a <c>finally</c>: a rolled-back transaction must not reach it.
    /// </summary>
    public Task AfterCommitAsync()
    {
        // The queued mark-handled statement is only durable now that the commit succeeded
        if (_queuedHandledUpdate && _context.Envelope != null)
        {
            _context.Envelope.Status = EnvelopeStatus.Handled;
        }

        // Under ServerlessWithOutbox this sends inline and deletes the outbox rows (GH-5016)
        return _context.FlushOutgoingMessagesAsync();
    }
}

/// <summary>
/// The mark-incoming-as-handled a session's transaction carries. <see cref="ExecuteAsync" /> runs it on any ADO.NET
/// driver; a store that queues SQL into its own batch instead (Marten) reads <see cref="Build" />.
/// </summary>
/// <param name="CheckHandledElsewhere">
/// GH-5423: false when this execution wrote its own Handled row for eager idempotency earlier in the session.
/// </param>
internal sealed record PendingInboxMark(
    IMessageDatabase Store,
    Envelope Envelope,
    DateTimeOffset KeepUntil,
    bool CheckHandledElsewhere)
{
    /// <summary>
    /// GH-4705: the statement comes from the message store rather than being written by each caller, so it matches
    /// the whole inbox identity, takes the partition-aware shape where there is one, and cannot drift from the one
    /// the store runs itself.
    /// </summary>
    public MarkAsHandledCommand Build(string idExpression, string uriExpression, string keepUntilExpression)
    {
        return Store.BuildMarkIncomingAsHandled(Envelope, KeepUntil, idExpression, uriExpression,
            keepUntilExpression);
    }

    /// <summary>
    /// Run the statement inside the session's transaction. Binds by name, which every driver Wolverine's document
    /// stores sit on accepts with an <c>@</c> prefix.
    /// </summary>
    public async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var markHandled = Build("@id", "@uri", "@keepUntil");

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = markHandled.Sql;
        addParameter(cmd, "@keepUntil", KeepUntil);
        addParameter(cmd, "@id", Envelope.Id);
        addParameter(cmd, "@uri", Envelope.Destination!.ToString());
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static void addParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
