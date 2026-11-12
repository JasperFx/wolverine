using Microsoft.Data.Sqlite;
using Fisher;
using Fisher.Services;
using Wolverine.Fisher.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Outbox;
using Wolverine.Sqlite;
using Wolverine.Runtime;

namespace Wolverine.Fisher;

/// <summary>
/// Fisher's listener adapter for <see cref="DocumentSessionOutboxCommit" />: before the save it enlists the inbox
/// mark in the session's transaction, and after the commit it releases the outgoing messages.
/// </summary>
internal class FlushOutgoingMessagesOnCommit : IDocumentSessionListener
{
    private readonly DocumentSessionOutboxCommit _commit;

    public FlushOutgoingMessagesOnCommit(MessageContext context, SqliteMessageStore messageStore)
    {
        _commit = new DocumentSessionOutboxCommit(context, messageStore);
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (_commit.StartSave() is { } mark)
        {
            session.AddTransactionParticipant(new MarkIncomingAsHandledParticipant(mark));
        }

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _commit.AfterCommitAsync();
    }
}

/// <summary>
/// Fisher's participant adapter for <see cref="DocumentSessionOutboxCommit" />, for a session that was already open
/// when FisherOutbox.Enroll saw it, because Fisher takes a session's listeners only when the session is opened.
/// Enroll adds this instead of the listener, so only one of the two ever runs for a session.
/// </summary>
internal class FlushOutgoingMessagesParticipant : ITransactionParticipant
{
    private readonly DocumentSessionOutboxCommit _commit;

    public FlushOutgoingMessagesParticipant(MessageContext context, SqliteMessageStore messageStore)
    {
        _commit = new DocumentSessionOutboxCommit(context, messageStore);
    }

    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        return _commit.StartSave() is { } mark
            ? mark.ExecuteAsync(connection, transaction, token)
            : Task.CompletedTask;
    }

    public Task AfterCommitAsync(CancellationToken token)
    {
        return _commit.AfterCommitAsync();
    }
}

/// <summary>
/// Writes nothing. SaveChangesAsync returns before it reaches any listener when the session has no work and no
/// participant, and a buffered message queues no work, so this is what carries a save holding nothing else as far
/// as <see cref="FlushOutgoingMessagesOnCommit" />.
/// </summary>
internal class KeepSaveChangesRunning : ITransactionParticipant
{
    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}

internal class MarkIncomingAsHandledParticipant : ITransactionParticipant
{
    private readonly PendingInboxMark _mark;

    public MarkIncomingAsHandledParticipant(PendingInboxMark mark)
    {
        _mark = mark;
    }

    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        return _mark.ExecuteAsync(connection, transaction, token);
    }
}
