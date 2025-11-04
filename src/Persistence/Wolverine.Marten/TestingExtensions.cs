using Marten;
using Marten.Events;
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
        where T : IDocumentStore
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
            await runtime.Services.PauseAllDaemonsAsync();
        });

        return configuration.AddStage(async (runtime, _, cancellation) =>
        {
            var exceptions = await runtime.Services.ForceAllMartenDaemonActivityToCatchUpAsync(cancellation, mode);
            foreach (var e in exceptions)
            {
                runtime.MessageTracking.LogException(e);
            }
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
        where T : IDocumentStore
    {
        configuration.BeforeExecution(async (runtime, cancellation) =>
        {
            await runtime.Services.PauseAllDaemonsAsync<T>();
        });

        return configuration.AddStage(async (runtime, _, cancellation) =>
        {
            var exceptions = await runtime.Services.ForceAllMartenDaemonActivityToCatchUpAsync<T>(cancellation, mode);
            foreach (var exception in exceptions)
            {
                runtime.MessageTracking.LogException(exception);
            }
        });
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
        this TrackedSessionConfiguration configuration, TimeSpan timeout) where T : IDocumentStore
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