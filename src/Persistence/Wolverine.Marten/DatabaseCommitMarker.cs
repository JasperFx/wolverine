using Marten;
using Marten.Services;
using Npgsql;
using Weasel.Storage;

namespace Wolverine.Marten;

/// <summary>
/// GH-5165 / GH-5166. Records when a Marten session's database commit has started and when it has succeeded.
///
/// <para>
/// Marten runs its session listeners' <c>AfterCommitAsync</c> inside <c>SaveChangesAsync</c>, after the database
/// commit. That includes Wolverine's own outbox flush, and a listener that throws there makes the call fail even though
/// the work is committed. From outside <c>SaveChangesAsync</c> the two failures look the same. This listener is
/// inserted at the FRONT of the session's listeners, so it hears about the commit before anything that could still
/// throw after it.
/// </para>
///
/// <para>
/// GH-5835. Before the listeners, Marten runs the transaction participants' <c>AfterCommitAsync</c> and then the
/// inline projection message batch's, and either can throw too. So the marker is also a transaction participant,
/// enlisted ahead of any the handler adds, and hears the commit straight after it. The listener stays for a session
/// whose commit runs no participants, such as one in an ambient transaction.
/// </para>
///
/// <para>
/// Public only because generated code creates it.
/// </para>
/// </summary>
public sealed class DatabaseCommitMarker : DocumentSessionListenerBase, ITransactionParticipant
{
    private readonly Action? _onCommitted;

    public DatabaseCommitMarker()
    {
    }

    /// <param name="onCommitted">Called once, when the database commit has succeeded and before any other listener runs</param>
    public DatabaseCommitMarker(Action onCommitted)
    {
        _onCommitted = onCommitted;
    }

    /// <summary>
    /// <c>SaveChangesAsync</c> has reached the commit: the events are processed and the listeners are about to run.
    /// A failure from here on, and before <see cref="Committed" />, is a failed commit.
    /// </summary>
    public bool CommitStarted { get; private set; }

    /// <summary>The database commit succeeded. Anything that fails after this did not roll the work back.</summary>
    public bool Committed { get; private set; }

    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        CommitStarted = true;
        return Task.CompletedTask;
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        markCommittedOnce();
        return Task.CompletedTask;
    }

    Task ITransactionParticipant<NpgsqlConnection, NpgsqlTransaction>.BeforeCommitAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken token) => Task.CompletedTask;

    Task ITransactionParticipant<NpgsqlConnection, NpgsqlTransaction>.AfterCommitAsync(CancellationToken token)
    {
        markCommittedOnce();
        return Task.CompletedTask;
    }

    private void markCommittedOnce()
    {
        if (Committed) return;

        Committed = true;
        _onCommitted?.Invoke();
    }

    /// <summary>
    /// Creates a marker, first among the listeners on <paramref name="session" /> and appended to its participants.
    /// </summary>
    public static DatabaseCommitMarker Enlist(IDocumentSession session) => enlist(session, new DatabaseCommitMarker());

    /// <summary>
    /// As <see cref="Enlist(IDocumentSession)" />, calling <paramref name="onCommitted" /> once the commit succeeds.
    /// </summary>
    public static DatabaseCommitMarker Enlist(IDocumentSession session, Action onCommitted)
        => enlist(session, new DatabaseCommitMarker(onCommitted));

    private static DatabaseCommitMarker enlist(IDocumentSession session, DatabaseCommitMarker marker)
    {
        session.Listeners.Insert(0, marker);
        if (session is ITransactionParticipantRegistrar participants) participants.AddTransactionParticipant(marker);
        return marker;
    }
}
