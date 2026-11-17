using JasperFx.Events;

namespace Wolverine.Runtime;

/// <summary>
///     The store agnostic half of each store's <c>NotifyObserverOfAppendedEvents</c> session listener:
///     tells <see cref="Agents.IWolverineObserver.EventsAppended" /> and the active tracked session which
///     streams a message's session committed.
/// </summary>
/// <remarks>
///     Only call this once the commit has succeeded. Polecat and Fisher used to tell the observer from
///     BeforeSaveChangesAsync, so an observer such as CritterWatch heard about events whose commit then
///     failed; Marten reported after the commit. GH-5143.
/// </remarks>
internal static class CommittedEventsReport
{
    /// <param name="context">The message context whose session committed.</param>
    /// <param name="streams">The streams the commit wrote.</param>
    /// <param name="store">The event store the session belongs to, when the integration can say (GH-4977).</param>
    public static void Report(MessageContext context, IReadOnlyList<StreamAction> streams, EventStoreIdentity? store)
    {
        if (streams.Count == 0) return;

        if (context.Runtime.Options.Tracking.EnableEventAppendTracking)
        {
            var events = streams.SelectMany(s => s.Events).ToList();
            if (events.Count != 0)
            {
                context.Runtime.Observer.EventsAppended(events);
            }
        }

        if (context.Runtime is WolverineRuntime runtime)
        {
            runtime.RecordAppendedEvents(context, streams, store);
        }
    }
}
