using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polecat;
using Wolverine.Polecat.Distribution;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Polecat;

public static class TestingExtensions
{
    /// <summary>
    /// Retrieve the Polecat IDocumentStore for the application
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    public static IDocumentStore DocumentStore(this IHost host)
    {
        return host.Services.GetRequiredService<IDocumentStore>();
    }

    /// <summary>
    /// Retrieve a specific Polecat IDocumentStore by type for the application
    /// </summary>
    /// <param name="host"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T DocumentStore<T>(this IHost host) where T : IDocumentStore
    {
        return host.Services.GetRequiredService<T>();
    }

    /// <summary>
    /// Retrieve the Polecat IDocumentStore for this service provider
    /// </summary>
    public static IDocumentStore DocumentStore(this IServiceProvider services)
    {
        return services.GetRequiredService<IDocumentStore>();
    }

    /// <summary>
    /// Retrieve a specific Polecat IDocumentStore by type for this service provider
    /// </summary>
    public static T DocumentStore<T>(this IServiceProvider services) where T : IDocumentStore
    {
        return services.GetRequiredService<T>();
    }

    /// <summary>
    /// Testing helper to pause all projection daemons in the system and completely
    /// disable the daemon projection assignments
    /// </summary>
    public static Task PauseAllDaemonsAsync(this IHost host)
    {
        return host.Services.PauseAllDaemonsAsync();
    }

    /// <summary>
    /// Testing helper to pause all projection daemons in the system and completely
    /// disable the daemon projection assignments
    /// </summary>
    public static Task PauseAllDaemonsAsync(this IServiceProvider services)
    {
        var coordinator = services.GetRequiredService<IProjectionCoordinator>();
        return coordinator.PauseAsync();
    }

    /// <summary>
    /// Testing helper to resume all projection daemons in the system and restart
    /// the daemon projection assignments
    /// </summary>
    public static Task ResumeAllDaemonsAsync(this IHost host)
    {
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();
        return coordinator.ResumeAsync();
    }

    /// <summary>
    /// Clean off all Polecat data in the default DocumentStore for this host
    /// </summary>
    public static async Task CleanAllPolecatDataAsync(this IHost host)
    {
        var store = host.DocumentStore().As<global::Polecat.DocumentStore>();
        await store.Advanced.CleanAllDocumentsAsync().ConfigureAwait(false);
        await store.Advanced.CleanAllEventDataAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Clean off all Polecat data in the default DocumentStore
    /// </summary>
    public static async Task CleanAllPolecatDataAsync(this IServiceProvider services)
    {
        var store = services.DocumentStore().As<global::Polecat.DocumentStore>();
        await store.Advanced.CleanAllDocumentsAsync().ConfigureAwait(false);
        await store.Advanced.CleanAllEventDataAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reset all data in the Polecat store. This also pauses, then resumes all asynchronous
    /// projection and subscription processing
    /// </summary>
    public static async Task ResetAllPolecatDataAsync(this IHost host)
    {
        await host.Services.ResetAllPolecatDataAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reset all data in the Polecat store. This also pauses, then resumes all asynchronous
    /// projection and subscription processing
    /// </summary>
    public static async Task ResetAllPolecatDataAsync(this IServiceProvider services)
    {
        var coordinator = services.GetService<IProjectionCoordinator>();
        if (coordinator != null)
        {
            await coordinator.PauseAsync().ConfigureAwait(false);
        }

        var store = services.DocumentStore().As<global::Polecat.DocumentStore>();
        await store.Advanced.CleanAllDocumentsAsync().ConfigureAwait(false);
        await store.Advanced.CleanAllEventDataAsync().ConfigureAwait(false);

        if (coordinator != null)
        {
            await coordinator.ResumeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Force any Polecat async daemons to immediately advance to the latest changes. This is strictly
    /// meant for test automation scenarios with small to medium sized databases
    /// </summary>
    public static Task<IReadOnlyList<Exception>> ForceAllPolecatDaemonActivityToCatchUpAsync(this IHost host,
        CancellationToken cancellation, CatchUpMode mode = CatchUpMode.AndResumeNormally)
    {
        return host.Services.ForceAllPolecatDaemonActivityToCatchUpAsync(cancellation, mode);
    }

    /// <summary>
    /// Force any Polecat async daemons to immediately advance to the latest changes. This is strictly
    /// meant for test automation scenarios with small to medium sized databases
    /// </summary>
    public static async Task<IReadOnlyList<Exception>> ForceAllPolecatDaemonActivityToCatchUpAsync(
        this IServiceProvider services,
        CancellationToken cancellation, CatchUpMode mode = CatchUpMode.AndResumeNormally)
    {
        var logger = services.GetService<ILogger<IProjectionCoordinator>>() ?? new NullLogger<IProjectionCoordinator>();
        var coordinator = services.GetRequiredService<IProjectionCoordinator>();

        // Has to be paused first
        await coordinator.PauseAsync().ConfigureAwait(false);

        var daemons = await coordinator.AllDaemonsAsync().ConfigureAwait(false);

        var list = new List<Exception>();

        foreach (var daemon in daemons)
        {
            try
            {
                await daemon.StopAllAsync().ConfigureAwait(false);
                await daemon.CatchUpAsync(cancellation).ConfigureAwait(false);

                logger.LogDebug("Executed a ProjectionDaemon.CatchUp() against {Daemon} in the main Polecat store",
                    daemon);
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "Error trying to execute a CatchUp on {Daemon} in the main Polecat store", daemon);
                list.Add(e);
            }
        }

        if (mode == CatchUpMode.AndResumeNormally)
        {
            await coordinator.StartAsync(cancellation).ConfigureAwait(false);
        }

        return list;
    }

    /// <summary>
    /// Reset all data in the main Polecat store before running the execution
    /// </summary>
    public static TrackedSessionConfiguration ResetAllPolecatDataFirst(
        this TrackedSessionConfiguration configuration)
    {
        return configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            await runtime.Services.ResetAllPolecatDataAsync();
        });
    }

    /// <summary>
    /// Force any Polecat projection or subscriptions normally running asynchronously
    /// to "catch up" immediately after running the main execution for the main
    /// Polecat store
    /// </summary>
    public static TrackedSessionConfiguration PauseThenCatchUpOnPolecatDaemonActivity(
        this TrackedSessionConfiguration configuration,
        CatchUpMode mode = CatchUpMode.AndResumeNormally)
    {
        configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            var envelope = new Envelope
            {
                MessageType = "Pause:Polecat:Daemons"
            };

            runtime.MessageTracking.ExecutionStarted(envelope);

            await runtime.Services.PauseAllDaemonsAsync();

            runtime.MessageTracking.ExecutionFinished(envelope);
        });

        return configuration.AddStage(async (runtime, _, cancellation) =>
        {
            var envelope = new Envelope
            {
                MessageType = "CatchUp:Polecat:DaemonActivity"
            };

            runtime.MessageTracking.ExecutionStarted(envelope);

            var coordinator = runtime.Services.GetRequiredService<IProjectionCoordinator>();
            var daemons = await coordinator.AllDaemonsAsync().ConfigureAwait(false);
            var subscriptions = new List<IDisposable>();
            var observer = new TrackedSessionShardWatcher(runtime);
            foreach (var daemon in daemons)
            {
                var subscription = daemon.Tracker.Subscribe(observer);
                subscriptions.Add(subscription);
            }

            try
            {
                var exceptions =
                    await runtime.Services.ForceAllPolecatDaemonActivityToCatchUpAsync(cancellation, mode);
                foreach (var exception in exceptions)
                {
                    runtime.MessageTracking.LogException(exception);
                }
            }
            finally
            {
                foreach (var subscription in subscriptions)
                {
                    subscription.SafeDispose();
                }
            }

            runtime.MessageTracking.ExecutionFinished(envelope);
        });
    }

    /// <summary>
    /// Wait for any ongoing Polecat asynchronous projection or subscription activity to finish
    /// after the main execution
    /// </summary>
    public static TrackedSessionConfiguration WaitForNonStaleDaemonDataAfterExecution(
        this TrackedSessionConfiguration configuration, TimeSpan timeout)
    {
        return configuration.AfterExecution(async (r, _) =>
        {
            try
            {
                var store = r.Services.DocumentStore().As<global::Polecat.DocumentStore>();
                await store.Database.WaitForNonStaleProjectionDataAsync(timeout);
            }
            catch (Exception e)
            {
                r.MessageTracking.LogException(e);
            }
        });
    }
}

/// <summary>
/// Controls whether projections and subscriptions should be restarted after the CatchUp operation
/// </summary>
public enum CatchUpMode
{
    /// <summary>
    /// Default setting, in this case the projections and subscriptions will be restarted in normal operation
    /// after the CatchUp operation is complete
    /// </summary>
    AndResumeNormally,

    /// <summary>
    /// Do not resume the asynchronous projection or synchronous behavior after the CatchUp operation is complete.
    /// This may be useful for test automation
    /// </summary>
    AndDoNothing
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
