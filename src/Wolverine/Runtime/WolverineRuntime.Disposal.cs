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

        Replies.Dispose();

        await Endpoints.DisposeAsync();

        Advanced.Cancel();

        ScheduledJobs?.Dispose();
    }
}
