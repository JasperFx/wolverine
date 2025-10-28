using JasperFx.Core.Reflection;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IAsyncDisposable
{
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (!_hasStopped)
        {
            await StopAsync(CancellationToken.None);
        }

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
    }
}