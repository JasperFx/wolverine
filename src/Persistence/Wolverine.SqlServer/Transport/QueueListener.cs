using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

internal class QueueListener : IListener
{
    private readonly SqlServerQueue _queue;
    
    public QueueListener(SqlServerQueue queue, IWolverineRuntime runtime, IReceiver receiver)
    {
        _queue = queue;
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Uri Address => _queue.Uri;
    public ValueTask StopAsync()
    {
        return ValueTask.CompletedTask;
    }
}