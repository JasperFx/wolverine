using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Baseline.ImTools;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IAsyncDisposable
{
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (!_hasStopped)
        {
            await StopAsync(CancellationToken.None);
        }

        foreach (var kv in _senders.Enumerate())
        {
            var sender = kv.Value;
            if (sender is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (sender is IDisposable d)
            {
                d.Dispose();
            }
        }

        foreach (var value in _listeners.Values)
        {
            await value.DisposeAsync();
        }

        Advanced.Cancel();

        ScheduledJobs?.Dispose();
    }
}
