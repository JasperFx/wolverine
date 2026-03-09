using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Runtime.Partitioning;

/// <summary>
/// Bridges an external transport listener to a companion local queue for global partitioning.
/// Messages received from the external transport are forwarded to the local durable queue
/// for sequential processing by GroupId.
/// </summary>
internal class GlobalPartitionedReceiverBridge : IReceiver
{
    private readonly ILocalQueue _localQueue;

    public GlobalPartitionedReceiverBridge(ILocalQueue localQueue)
    {
        _localQueue = localQueue;
    }

    public IHandlerPipeline Pipeline => _localQueue.Pipeline;

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        foreach (var message in messages)
        {
            await ReceivedAsync(listener, message);
        }
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        // Forward to local queue for sequential processing
        await _localQueue.ReceivedAsync(listener, envelope);
    }

    public ValueTask DrainAsync()
    {
        // The local queue handles its own draining
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        // Don't dispose the local queue - it's managed elsewhere
    }
}
