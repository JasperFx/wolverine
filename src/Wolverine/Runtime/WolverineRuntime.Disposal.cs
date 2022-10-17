using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline;

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

        await Options.Transports.As<IAsyncDisposable>().DisposeAsync();

        Advanced.Cancel();

        ScheduledJobs?.Dispose();
    }
}