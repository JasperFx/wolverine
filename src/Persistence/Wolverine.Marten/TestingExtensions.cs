using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Marten;

public static class TestingExtensions
{
    /// <summary>
    /// Reset all data in the main Marten store before running the execution
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration ResetAllMartenDataFirst(this TrackedSessionConfiguration configuration)
    {
        return configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            await runtime.Services.ResetAllMartenDataAsync();
        });
    }

    /// <summary>
    /// Reset all data in an ancillary Marten store before running the execution
    /// </summary>
    /// <param name="configuration"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static TrackedSessionConfiguration ResetAllMartenDataFirst<T>(this TrackedSessionConfiguration configuration)
        where T : class, IDocumentStore
    {
        return configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            await runtime.Services.ResetAllMartenDataAsync<T>();
        });
    }
    
    /// <summary>
    /// Force any Marten projection or subscriptions normally running asynchronously
    /// to "catch up" immediately after running the main execution for the main
    /// Marten store
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="mode"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration PauseThenCatchUpOnMartenDaemonActivity(this TrackedSessionConfiguration configuration, CatchUpMode mode = CatchUpMode.AndResumeNormally)
    {
        configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            var envelope = new Envelope
            {
                MessageType = "Pause:Marten:Daemons"
            };
            
            runtime.MessageTracking.ExecutionStarted(envelope);
            
            await runtime.Services.PauseAllDaemonsAsync();
            
            runtime.MessageTracking.ExecutionFinished(envelope);
        });

        return configuration.AddStage(async (runtime, _, cancellation) =>
        {
            var envelope = new Envelope
            {
                MessageType = "CatchUp:Marten:DaemonActivity"
            };

            var coordinator = runtime.Services.GetRequiredService<IProjectionCoordinator>();

            await catchUpThroughCoordinatorAsync(runtime, envelope, coordinator,
                () => runtime.Services.DocumentStore().WaitForNonStaleProjectionDataAsync(CatchUpTimeout), mode,
                "the main Marten store", cancellation);
        });
    }

    /// <summary>
    /// Force any Marten projection or subscriptions normally running asynchronously
    /// to "catch up" immediately after running the main execution for an ancillary
    /// Marten store
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="mode"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static TrackedSessionConfiguration PauseThenCatchUpOnMartenDaemonActivity<T>(this TrackedSessionConfiguration configuration, CatchUpMode mode = CatchUpMode.AndResumeNormally)
        where T : class, IDocumentStore
    {
        configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            var envelope = new Envelope
            {
                MessageType = "Pause:Marten:Daemons:" + typeof(T).NameInCode()
            };
            
            runtime.MessageTracking.ExecutionStarted(envelope);
            
            await runtime.Services.PauseAllDaemonsAsync<T>();
            
            runtime.MessageTracking.ExecutionFinished(envelope);
        });

        return configuration.AddStage(async (runtime, _, cancellation) =>
        {
            var envelope = new Envelope
            {
                MessageType = "CatchUp:Marten:DaemonActivity:" + typeof(T).FullNameInCode()
            };

            var coordinator = runtime.Services.GetRequiredService<IProjectionCoordinator<T>>();

            await catchUpThroughCoordinatorAsync(runtime, envelope, coordinator,
                () => runtime.Services.DocumentStore<T>().WaitForNonStaleProjectionDataAsync(CatchUpTimeout), mode,
                $"the ancillary Marten store {typeof(T).NameInCode()}", cancellation);
        });
    }

    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Force every Marten projection and subscription running asynchronously under Wolverine-managed event
    /// subscription distribution to catch up to the current high water mark, then restore the daemons to the
    /// state named by <paramref name="mode"/>.
    ///
    /// This is the standalone equivalent of
    /// <see cref="PauseThenCatchUpOnMartenDaemonActivity(TrackedSessionConfiguration,CatchUpMode)"/>, for suites
    /// that are not driving the work through <c>TrackActivity()</c>. Marten's own
    /// <c>IHost.ForceAllMartenDaemonActivityToCatchUpAsync()</c> is deliberately a passive read-only wait when
    /// the store is <c>DaemonMode.ExternallyManaged</c> — which is the mode Wolverine puts it in — and punts
    /// active catch-up to the external coordinator. This is that path.
    ///
    /// Note what it does NOT do: it never calls <c>IProjectionDaemon.CatchUpAsync</c>. Doing that while the
    /// coordinator owns the shards introduces a *second* writer of each progression row, which is what produces
    /// the <c>ProgressionProgressOutOfOrderException</c> ("multiple processes try to process the projection")
    /// and the <c>23505 duplicate key value violates unique constraint "pk_mt_event_progression"</c> on a
    /// shard's first progression row. Resuming the agents that already own those shards and waiting for
    /// non-stale data means there is only ever one writer, so neither race exists to be swallowed.
    /// </summary>
    /// <param name="host"></param>
    /// <param name="mode">Whether to leave the daemons running afterwards (the default) or re-pause them</param>
    /// <param name="timeout">How long to allow for catch-up. Defaults to 60 seconds.</param>
    /// <param name="cancellation"></param>
    public static Task PauseThenCatchUpOnMartenDaemonActivityAsync(this IHost host,
        CatchUpMode mode = CatchUpMode.AndResumeNormally, TimeSpan? timeout = null,
        CancellationToken cancellation = default)
    {
        return host.Services.PauseThenCatchUpOnMartenDaemonActivityAsync(mode, timeout, cancellation);
    }

    /// <summary>
    /// Force every Marten projection and subscription running asynchronously under Wolverine-managed event
    /// subscription distribution to catch up to the current high water mark, then restore the daemons to the
    /// state named by <paramref name="mode"/>. See the <c>IHost</c> overload for the full rationale.
    /// </summary>
    public static Task PauseThenCatchUpOnMartenDaemonActivityAsync(this IServiceProvider services,
        CatchUpMode mode = CatchUpMode.AndResumeNormally, TimeSpan? timeout = null,
        CancellationToken cancellation = default)
    {
        var coordinator = services.GetRequiredService<IProjectionCoordinator>();

        return resumeAndWaitForNonStaleAsync(coordinator,
            t => services.DocumentStore().WaitForNonStaleProjectionDataAsync(t),
            mode, timeout ?? CatchUpTimeout, "the main Marten store", cancellation);
    }

    /// <summary>
    /// Force every Marten projection and subscription in an ancillary store running asynchronously under
    /// Wolverine-managed event subscription distribution to catch up to the current high water mark, then
    /// restore the daemons to the state named by <paramref name="mode"/>. See the non-generic <c>IHost</c>
    /// overload for the full rationale.
    /// </summary>
    public static Task PauseThenCatchUpOnMartenDaemonActivityAsync<T>(this IHost host,
        CatchUpMode mode = CatchUpMode.AndResumeNormally, TimeSpan? timeout = null,
        CancellationToken cancellation = default) where T : class, IDocumentStore
    {
        return host.Services.PauseThenCatchUpOnMartenDaemonActivityAsync<T>(mode, timeout, cancellation);
    }

    /// <summary>
    /// Force every Marten projection and subscription in an ancillary store running asynchronously under
    /// Wolverine-managed event subscription distribution to catch up to the current high water mark, then
    /// restore the daemons to the state named by <paramref name="mode"/>. See the non-generic <c>IHost</c>
    /// overload for the full rationale.
    /// </summary>
    public static Task PauseThenCatchUpOnMartenDaemonActivityAsync<T>(this IServiceProvider services,
        CatchUpMode mode = CatchUpMode.AndResumeNormally, TimeSpan? timeout = null,
        CancellationToken cancellation = default) where T : class, IDocumentStore
    {
        var coordinator = services.GetRequiredService<IProjectionCoordinator<T>>();

        return resumeAndWaitForNonStaleAsync(coordinator,
            t => services.DocumentStore<T>().WaitForNonStaleProjectionDataAsync(t),
            mode, timeout ?? CatchUpTimeout, $"the ancillary Marten store {typeof(T).NameInCode()}", cancellation);
    }

    // The whole of "force catch-up" under a Wolverine-managed coordinator: make sure the agents that own the
    // shards are running, wait for every (database, tenant) shard to reach the current high water mark, then
    // honor the caller's CatchUpMode. Same algorithm the tracked-session stage runs in
    // catchUpThroughCoordinatorAsync below, minus the message-tracking bookkeeping that only makes sense
    // inside a TrackedSession.
    private static async Task resumeAndWaitForNonStaleAsync(
        global::Marten.Events.Daemon.Coordination.IProjectionCoordinator coordinator,
        Func<TimeSpan, Task> waitForNonStale,
        CatchUpMode mode,
        TimeSpan timeout,
        string storeDescription,
        CancellationToken cancellation)
    {
        await coordinator.ResumeAsync().ConfigureAwait(false);

        try
        {
            await waitForNonStale(timeout).WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Cancelled before the Marten projections for {storeDescription} caught up. The catch-up itself was allowed {timeout.TotalSeconds:N0} seconds.");
        }
        finally
        {
            if (mode == CatchUpMode.AndDoNothing)
            {
                await coordinator.PauseAsync().ConfigureAwait(false);
            }
        }
    }

    // GH-3349 / Marten #4904: under Wolverine-managed event-subscription distribution the Marten store is
    // DaemonMode.ExternallyManaged, so Marten's ForceAllMartenDaemonActivityToCatchUpAsync no longer DRIVES
    // a paused daemon forward — it degrades to a passive wait for non-stale data (to avoid a
    // ProgressionProgressOutOfOrderException race with the external supervisor) and explicitly punts active
    // catch-up to "the external coordinator", i.e. Wolverine. The pre-#4904 helper paused the daemons and
    // relied on ForceAll to force them; under 9.14.0 that call just waits on the paused agents and blocks
    // until timeout. Drive catch-up through the Wolverine coordinator instead: resume the agents so they
    // process the events appended while paused, wait until the projections reach the current high-water
    // mark, then honor the CatchUpMode (AndDoNothing re-pauses; AndResumeNormally leaves the agents running).
    // Uniform across a Wolverine-managed coordinator and a Marten-owned one, since both expose the same
    // IProjectionCoordinator Resume/Pause contract.
    private static async Task catchUpThroughCoordinatorAsync(
        IWolverineRuntime runtime,
        Envelope envelope,
        global::Marten.Events.Daemon.Coordination.IProjectionCoordinator coordinator,
        Func<Task> waitForNonStale,
        CatchUpMode mode,
        string storeDescription,
        CancellationToken cancellation)
    {
        runtime.MessageTracking.ExecutionStarted(envelope);

        var subscriptions = new List<IDisposable>();
        var observer = new TrackedSessionShardWatcher(runtime);

        try
        {
            await coordinator.ResumeAsync().ConfigureAwait(false);

            var daemons = await coordinator.AllDaemonsAsync().ConfigureAwait(false);
            foreach (var daemon in daemons)
            {
                subscriptions.Add(daemon.Tracker.Subscribe(observer));
            }

            // GH-3388: the tracked session that owns this stage cancels at its OWN timeout
            // (TrackedSession.Timeout, 5 seconds by default), which is far shorter than
            // CatchUpTimeout. Waiting on the un-cancellable inner task meant the session gave up
            // first and the catch-up envelope was simply left un-finished — surfacing as an opaque
            // "started but never finished" hang rather than something a user could act on. Honor
            // the session's token and say plainly what to do about it.
            try
            {
                await waitForNonStale().WaitAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The tracked session timed out before the Marten projections for {storeDescription} caught up. The catch-up itself is allowed {CatchUpTimeout.TotalSeconds:N0} seconds, but the tracked session's own Timeout governs it. Raise it with TrackActivity().Timeout(...) — projection catch-up on a cold daemon, a large event backlog, or a busy CI machine routinely needs more than the 5 second default.");
            }

            if (mode == CatchUpMode.AndDoNothing)
            {
                await coordinator.PauseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            runtime.MessageTracking.LogException(e);
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.SafeDispose();
            }
        }

        runtime.MessageTracking.ExecutionFinished(envelope);
    }

    /// <summary>
    /// Wait for any ongoing Marten asynchronous projection or subscription activity to finish
    /// after the main execution
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration WaitForNonStaleDaemonDataAfterExecution(
        this TrackedSessionConfiguration configuration, TimeSpan timeout)
    {
        return configuration.AfterExecution(async (r, _) =>
        {
            try
            {
                await r.Services.DocumentStore().WaitForNonStaleProjectionDataAsync(timeout);
            }
            catch (Exception e)
            {
                r.MessageTracking.LogException(e);
            }
        });
    }
    
    /// <summary>
    /// Wait for any ongoing Marten asynchronous projection or subscription activity to finish
    /// after the main execution for an ancillary Marten store
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static TrackedSessionConfiguration WaitForNonStaleDaemonDataAfterExecution<T>(
        this TrackedSessionConfiguration configuration, TimeSpan timeout) where T : class, IDocumentStore
    {
        return configuration.AfterExecution(async (r, _) =>
        {
            try
            {
                await r.Services.DocumentStore<T>().WaitForNonStaleProjectionDataAsync(timeout);
            }
            catch (Exception e)
            {
                r.MessageTracking.LogException(e);
            }
        });
    }
}

internal class TrackedSessionShardWatcher : IObserver<ShardState>
{
    private readonly IWolverineRuntime _runtime;

    public TrackedSessionShardWatcher(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public void OnCompleted()
    {
        
    }

    public void OnError(Exception error)
    {

    }

    public void OnNext(ShardState value)
    {
        _runtime.MessageTracking.LogStatus(value.ToString());
    }
}