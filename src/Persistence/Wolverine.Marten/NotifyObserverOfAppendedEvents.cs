using JasperFx.Events;
using Marten;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// Marten outbox-session listener that reports the events committed by an outbox-enrolled
/// message/endpoint. Registered when <see cref="WolverineOptions.Tracking"/>.<c>EnableEventAppendTracking</c>
/// is on, for <c>IWolverineObserver.EventsAppended</c>, or while a tracked session is active (GH-4931), for
/// <c>ITrackedSession.AppendedEvents</c>. It keeps the Marten dependency inside the Wolverine.Marten
/// integration, so observers (e.g. CritterWatch) attribute appended events to the executing handler/endpoint
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
        // GH-4977: the store, so an act on a host with ancillary stores knows which ones it heard from
        CommittedEventsReport.Report(_context, commit.GetStreams().ToList(),
            (session.DocumentStore as JasperFx.Events.IEventStore)?.Identity);

        return Task.CompletedTask;
    }
}
