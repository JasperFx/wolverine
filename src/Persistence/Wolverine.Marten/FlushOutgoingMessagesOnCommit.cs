using Marten;
using Marten.Services;
using Wolverine.Marten.Persistence.Operations;
using Wolverine.Postgresql;
using Wolverine.RDBMS.Outbox;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// Marten's adapter for <see cref="DocumentSessionOutboxCommit" />. Marten queues the inbox mark into its own batch
/// rather than running it on the connection, so this reads the statements off the pending mark.
/// </summary>
internal class FlushOutgoingMessagesOnCommit : DocumentSessionListenerBase
{
    private readonly DocumentSessionOutboxCommit _commit;

    public FlushOutgoingMessagesOnCommit(MessageContext context, PostgresqlMessageStore messageStore)
    {
        _commit = new DocumentSessionOutboxCommit(context, messageStore);
    }

    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (_commit.StartSave() is not { } mark) return Task.CompletedTask;

        // GH-4705: QueueSqlCommand binds positionally, so the store hands back the arguments in placeholder order
        // rather than this method trying to mirror it.
        //
        // GH-4736: and ONE STATEMENT AT A TIME. QueueSqlCommand rejects any SQL containing a ';', so the
        // partition-aware shape -- a DELETE followed by an UPDATE -- could never pass through it as a single string:
        // under EnableInboxPartitioning every durable-inbox message whose handler committed through a Marten
        // session was dead-lettered. Marten runs queued commands in the order they were queued, which is the order
        // the DELETE and the UPDATE need.
        var markHandled = mark.Build("?", "?", "?");
        foreach (var statement in markHandled.Statements)
        {
            session.QueueSqlCommand(statement.Sql, statement.Arguments);
        }

        // GH-5423: and refuse to commit work another execution has already finished. Not for eager idempotency,
        // which wrote this execution's own Handled row earlier in the same session
        if (mark.CheckHandledElsewhere && mark.Store is PostgresqlMessageStore store)
        {
            var check = store.BuildHandledElsewhereCheck(mark.Envelope, mark.KeepUntil, "?", "?", "?");
            session.QueueOperation(new AssertNotHandledElsewhere(check, mark.Envelope));
        }

        return Task.CompletedTask;
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _commit.AfterCommitAsync();
    }
}
