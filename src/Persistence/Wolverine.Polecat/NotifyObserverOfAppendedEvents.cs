using JasperFx.Events;
using Polecat;
using Polecat.Services;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
/// Polecat outbox-session listener that reports the events appended by an outbox-enrolled message/endpoint.
/// Registered when <see cref="WolverineOptions.Tracking"/>.<c>EnableEventAppendTracking</c> is on, for
/// <c>IWolverineObserver.EventsAppended</c>, or while a tracked session is active (GH-4931), for
/// <c>ITrackedSession.AppendedEvents</c>. The Polecat half of the store-agnostic event-append observation: it
/// keeps the Polecat dependency inside the Wolverine.Polecat integration so observers (e.g. CritterWatch) attribute
/// appended events to the executing handler/endpoint through the Wolverine abstraction only. The observer
/// reads <c>session.PendingChanges.Streams</c> in <see cref="BeforeSaveChangesAsync"/>, as
/// <see cref="PublishIncomingEventsBeforeCommit"/> does; a tracked session hears about the same streams only
/// in <see cref="AfterCommitAsync"/>, so a commit that fails records nothing.
/// </summary>
internal class NotifyObserverOfAppendedEvents : IDocumentSessionListener
{
    private readonly MessageContext _context;
    private IReadOnlyList<StreamAction> _pending = Array.Empty<StreamAction>();

    public NotifyObserverOfAppendedEvents(MessageContext context)
    {
        _context = context;
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        _pending = session.PendingChanges.Streams.ToList();

        if (_context.Runtime.Options.Tracking.EnableEventAppendTracking)
        {
            var events = _pending.SelectMany(s => s.Events).ToList();
            if (events.Count != 0)
            {
                _context.Runtime.Observer.EventsAppended(events);
            }
        }

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var committed = _pending;
        _pending = Array.Empty<StreamAction>();

        if (_context.Runtime is WolverineRuntime runtime)
        {
            runtime.RecordAppendedEvents(_context, committed);
        }

        return Task.CompletedTask;
    }
}
