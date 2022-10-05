using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wolverine.Runtime;

internal partial class WolverineRuntime : IAsyncDisposable
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