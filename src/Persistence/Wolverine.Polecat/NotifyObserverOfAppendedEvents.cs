using JasperFx.Events;
using Polecat;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
/// Polecat outbox-session listener (registered only when
/// <see cref="WolverineOptions.Tracking"/>.<c>EnableEventAppendTracking</c> is on) that reports the
/// events pending in an outbox-enrolled message/endpoint to
/// <c>IWolverineObserver.EventsAppended</c>. The Polecat half of the store-agnostic event-append
/// observation: it keeps the Polecat dependency inside the Wolverine.Polecat integration so
/// observers (e.g. CritterWatch) attribute appended events to the executing handler/endpoint
/// through the Wolverine abstraction only. Polecat's <c>AfterCommitAsync</c> carries no change-set,
/// so (like <see cref="PublishIncomingEventsBeforeCommit"/>) this reads
/// <c>session.PendingChanges.Streams</c> in <see cref="BeforeSaveChangesAsync"/>.
/// </summary>
internal class NotifyObserverOfAppendedEvents : IDocumentSessionListener
{
    private readonly MessageContext _context;

    public NotifyObserverOfAppendedEvents(MessageContext context)
    {
        _context = context;
    }

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var events = session.PendingChanges.Streams.SelectMany(s => s.Events).ToList();
        if (events.Count != 0)
        {
            _context.Runtime.Observer.EventsAppended(events);
        }

        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
