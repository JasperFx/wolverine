using Marten;
using Marten.Services;

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
/// Public only because generated code creates it.
/// </para>
/// </summary>
public sealed class DatabaseCommitMarker : DocumentSessionListenerBase
{
    private readonly Action? _onCommitted;

    public DatabaseCommitMarker()
    {
    }

    /// <param name="onCommitted">Called once the database commit has succeeded, before any other listener runs</param>
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
        Committed = true;
        _onCommitted?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Creates a marker and puts it ahead of every other listener on <paramref name="session" />.</summary>
    public static DatabaseCommitMarker Enlist(IDocumentSession session)
    {
        var marker = new DatabaseCommitMarker();
        session.Listeners.Insert(0, marker);
        return marker;
    }

    /// <summary>
    /// Creates a marker that calls <paramref name="onCommitted" /> once the database commit succeeds, and puts it
    /// ahead of every other listener on <paramref name="session" />.
    /// </summary>
    public static DatabaseCommitMarker Enlist(IDocumentSession session, Action onCommitted)
    {
        var marker = new DatabaseCommitMarker(onCommitted);
        session.Listeners.Insert(0, marker);
        return marker;
    }
}
