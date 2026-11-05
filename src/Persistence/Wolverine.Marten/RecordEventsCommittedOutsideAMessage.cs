using Marten;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// GH-5385. A store-wide session listener that lets a tracked session hear the events committed through a session
/// Wolverine did not open: a Razor page's, a Minimal API's or a controller's own injected <c>IDocumentSession</c>.
/// Those events belong to the act, as the ones a handler's session commits do, so <c>ThenEmitted</c> after
/// <c>WhenTracked</c> sees them. Does nothing, cheaply, unless a tracked session is active; and does nothing for a
/// session carrying <see cref="NotifyObserverOfAppendedEvents" />, which already reports its own commit, attributed to
/// its message.
/// </summary>
internal sealed class RecordEventsCommittedOutsideAMessage(IServiceProvider services) : DocumentSessionListenerBase
{
    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        if (services.GetService(typeof(IWolverineRuntime)) is not WolverineRuntime { ActiveSession: not null } runtime)
        {
            return Task.CompletedTask;
        }

        if (session.Listeners.Any(x => x is NotifyObserverOfAppendedEvents)) return Task.CompletedTask;

        runtime.RecordAppendedEventsOutsideAMessage(commit.GetStreams().ToList(),
            (session.DocumentStore as JasperFx.Events.IEventStore)?.Identity, session.TenantId);

        return Task.CompletedTask;
    }
}
