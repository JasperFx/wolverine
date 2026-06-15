using JasperFx.Events;
using Marten;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// Marten outbox-session listener (registered only when
/// <see cref="WolverineOptions.Tracking"/>.<c>EnableEventAppendTracking</c> is on) that reports the
/// events committed by an outbox-enrolled message/endpoint to
/// <c>IWolverineObserver.EventsAppended</c>. This is the store-specific half of the store-agnostic
/// event-append observation: it keeps the Marten dependency inside the Wolverine.Marten integration,
/// so observers (e.g. CritterWatch) attribute appended events to the executing handler/endpoint
/// through the Wolverine abstraction only — appended events never hit the message outbox, so message
/// causation can't see them. Mirrors <see cref="PublishIncomingEventsBeforeCommit"/>'s wiring.
/// </summary>
internal class NotifyObserverOfAppendedEvents : DocumentSessionListenerBase
{
    private readonly MessageContext _context;

    public NotifyObserverOfAppendedEvents(MessageContext context)
    {
        _context = context;
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var events = commit.GetEvents().ToList();
        if (events.Count != 0)
        {
            _context.Runtime.Observer.EventsAppended(events);
        }

        return Task.CompletedTask;
    }
}
