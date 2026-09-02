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
    }
}