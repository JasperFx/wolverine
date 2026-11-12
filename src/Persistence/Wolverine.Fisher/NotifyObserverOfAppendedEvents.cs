using JasperFx.Events;
using Fisher;
using Fisher.Services;
using Wolverine.Runtime;

namespace Wolverine.Fisher;

/// <summary>
/// Fisher outbox-session listener that reports the events appended by an outbox-enrolled message/endpoint.
/// Registered when <see cref="WolverineOptions.Tracking"/>.<c>EnableEventAppendTracking</c> is on, for
/// <c>IWolverineObserver.EventsAppended</c>, or while a tracked session is active (GH-4931), for
/// <c>ITrackedSession.AppendedEvents</c>. The Fisher half of the store-agnostic event-append observation: it
/// keeps the Fisher dependency inside the Wolverine.Fisher integration so observers (e.g. CritterWatch) attribute
/// appended events to the executing handler/endpoint through the Wolverine abstraction only. The observer
/// captures <c>session.Events.PendingStreams</c> in <see cref="BeforeSaveChangesAsync"/>, as
/// <see cref="PublishIncomingEventsBeforeCommit"/> does, and reports them through the shared
/// <see cref="CommittedEventsReport"/> only in <see cref="AfterCommitAsync"/>, so a commit that fails reports
/// nothing to the observer or the tracked session (GH-5143).
/// </summary>
internal class NotifyObserverOfAppendedEvents : IDocumentSessionListener
{
    private readonly MessageContext _context;
    private IReadOnlyList<StreamAction> _pending = Array.Empty<StreamAction>();

    private readonly EventStoreIdentity? _store;

    /// <param name="store">GH-4977: the event store the session belongs to, when the factory knows it.</param>
    public NotifyObserverOfAppendedEvents(MessageContext context, EventStoreIdentity? store = null)
    {
        _context = context;
        _store = store;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDocumentSession, object> _reporting = new();

    /// <summary>GH-5390. True once this listener has run for the session, i.e. it reports the commit itself.</summary>
    internal static bool IsReportedByItsOwnListener(IDocumentSession session) => _reporting.TryGetValue(session, out _);

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        _reporting.AddOrUpdate(session, this);
        // Capture only. The observer and the tracked session hear about these streams after the commit
        // succeeds, never before it (GH-5143).
        _pending = session.Events.PendingStreams.ToList();

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var committed = _pending;
        _pending = Array.Empty<StreamAction>();

        // GH-4977: the store, so an act on a host with ancillary stores knows which ones it heard from
        CommittedEventsReport.Report(_context, committed, _store);

        return Task.CompletedTask;
    }
}
