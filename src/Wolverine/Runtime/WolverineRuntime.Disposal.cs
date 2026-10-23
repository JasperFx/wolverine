using JasperFx.Core.Reflection;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IAsyncDisposable
{
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        // Unconditional, and this is the point of StopAsync being joinable rather than skippable: a
        // shutdown another caller is still running has to FINISH before the endpoints and transports
        // below are torn out from under it. StopAsync is single-entry, so this either drives the
        // shutdown or waits for the one already in flight.
        await StopAsync(CancellationToken.None);

        Replies.Dispose();

        await Endpoints.DisposeAsync();

        await Options.Transports.As<IAsyncDisposable>().DisposeAsync();

        DurabilitySettings.Cancel();

        if (DurableScheduledJobs != null)
        {
            await DurableScheduledJobs.StopAsync(CancellationToken.None);
        }

        // The recurring-message agent started directly on a storeless host (no node coordination
        // to own its lifecycle). Null everywhere else — the agent runtime stops it there.
        if (InMemoryRecurringAgent != null)
        {
            await InMemoryRecurringAgent.StopAsync(CancellationToken.None);
        }

        // GH-5168. The timer agents started directly beside it on the same kind of host.
        if (InMemoryRecurringTimers != null)
        {
            await InMemoryRecurringTimers.StopAllLocallyAsync();
        }

        if (ScheduledJobs != null)
        {
            ScheduledJobs.Dispose();
        }

        foreach (var definition in Options.BatchDefinitions)
        {
            await definition.As<IAsyncDisposable>().DisposeAsync();
        }

        if (_accumulator.IsValueCreated)
        {
            await _accumulator.Value.DisposeAsync();
        }

        if (_sagaStorage.IsValueCreated && _sagaStorage.Value is IDisposable d)
        {
            d.Dispose();
        }

        // GH-5012: a Meter lives in a process-wide static registry until it is disposed, and the
        // persistence gauges on it hold callbacks back into this runtime. Without this, every
        // disposed host stays reachable for the life of the process.
        Meter.Dispose();
    }
}