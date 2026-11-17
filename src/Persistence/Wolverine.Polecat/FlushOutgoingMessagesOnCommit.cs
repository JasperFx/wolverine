using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Polecat;
using Polecat.Services;
using Wolverine.Polecat.Persistence.Operations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Outbox;
using Wolverine.SqlServer.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
/// Polecat's adapter for <see cref="DocumentSessionOutboxCommit" />: before the save it enlists the inbox mark in the
/// session's transaction, and after the commit it releases the outgoing messages.
/// </summary>
internal class FlushOutgoingMessagesOnCommit : IDocumentSessionListener
{
    private readonly DocumentSessionOutboxCommit _commit;

    public FlushOutgoingMessagesOnCommit(MessageContext context, SqlServerMessageStore messageStore)
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
/// Runs <see cref="FlushOutgoingMessagesOnCommit" /> for a session that was already open when it was enrolled in
/// the outbox. Polecat takes a session's listeners only when the session is opened, and its
/// ITransactionParticipant has no after-commit hook, so PolecatOutbox.Enroll has nothing of its own to attach to.
/// Every store integrated with Wolverine carries one of these instead, and it does nothing for any other session.
/// </summary>
/// <remarks>
/// Weakly keyed, so an enrolled session is collected as it would have been. JasperFx 3's AddSaveListener lets
/// Enroll add the listener to the session itself, as Wolverine.Marten does, and this goes away.
/// </remarks>
internal class FlushEnrolledOutboxOnCommit : IDocumentSessionListener
{
    private static readonly ConditionalWeakTable<IDocumentSession, FlushOutgoingMessagesOnCommit> _enrolled = new();

    public static void Enroll(IDocumentSession session, FlushOutgoingMessagesOnCommit listener)
    {
        _enrolled.AddOrUpdate(session, listener);
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        return _enrolled.TryGetValue(session, out var listener)
            ? listener.BeforeSaveChangesAsync(session, token)
            : Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _enrolled.TryGetValue(session, out var listener)
            ? listener.AfterCommitAsync(session, commit, token)
            : Task.CompletedTask;
    }
}

/// <summary>
/// Writes nothing. SaveChangesAsync returns before it reaches any listener when the session has no work and no
/// participant, and a buffered message queues no work, so this is what carries a save holding nothing else as far
/// as <see cref="FlushOutgoingMessagesOnCommit" />.
/// </summary>
internal class KeepSaveChangesRunning : ITransactionParticipant
{
    public Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
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

    public Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        return _mark.ExecuteAsync(connection, transaction, token);
    }
}
