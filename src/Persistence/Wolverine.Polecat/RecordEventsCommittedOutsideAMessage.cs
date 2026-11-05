using System.Runtime.CompilerServices;
using JasperFx.Events;
using Polecat;
using Polecat.Services;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
/// GH-5390. The Polecat twin of the Marten listener from GH-5385: a store-wide session listener that lets a tracked
/// session hear the events committed through a session Wolverine did not open (a Razor page's, a Minimal API's or a
/// controller's own injected <c>IDocumentSession</c>). Those events belong to the act, as the ones a handler's session
/// commits do, so <c>ThenEmitted</c> after <c>WhenTracked</c> sees them. Does nothing, cheaply, unless a tracked session
/// is active; and does nothing for a session carrying <see cref="NotifyObserverOfAppendedEvents" />, which already
/// reports its own commit, attributed to its message.
/// </summary>
/// <remarks>
/// A store-wide listener is shared by every session, so the streams read before the save wait here, weakly keyed by
/// their session, until that session's commit succeeds: a commit that fails records nothing.
/// </remarks>
internal sealed class RecordEventsCommittedOutsideAMessage(IServiceProvider services, Type? storeType = null)
    : IDocumentSessionListener
{
    private readonly ConditionalWeakTable<IDocumentSession, IReadOnlyList<StreamAction>> _pending = new();

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (!isRecording()) return Task.CompletedTask;

        _pending.AddOrUpdate(session, session.PendingChanges.Streams.ToList());
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        if (!_pending.TryGetValue(session, out var streams)) return Task.CompletedTask;
        _pending.Remove(session);

        // Every before-save has run by now, so a session that reports its own commit has said so
        if (NotifyObserverOfAppendedEvents.IsReportedByItsOwnListener(session)) return Task.CompletedTask;

        if (services.GetService(typeof(IWolverineRuntime)) is WolverineRuntime { ActiveSession: not null } runtime)
        {
            runtime.RecordAppendedEventsOutsideAMessage(streams, (services.GetService(storeType ?? typeof(IDocumentStore)) as IEventStore)?.Identity, session.TenantId);
        }

        return Task.CompletedTask;
    }

    private bool isRecording()
    {
        return services.GetService(typeof(IWolverineRuntime)) is WolverineRuntime { ActiveSession: not null };
    }
}
