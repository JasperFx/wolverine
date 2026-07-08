using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.DependencyInjection;
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
                () => runtime.Services.DocumentStore().WaitForNonStaleProjectionDataAsync(CatchUpTimeout), mode);
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
                () => runtime.Services.DocumentStore<T>().WaitForNonStaleProjectionDataAsync(CatchUpTimeout), mode);
        });
    }

    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(60);

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
        CatchUpMode mode)
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

            await waitForNonStale().ConfigureAwait(false);

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