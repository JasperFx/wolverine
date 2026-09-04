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
    private readonly bool _envelopesArePersistedInInbox;

    public GlobalPartitionedReceiverBridge(ILocalQueue localQueue, bool envelopesArePersistedInInbox = false)
    {
        _localQueue = localQueue;
        _envelopesArePersistedInInbox = envelopesArePersistedInInbox;
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
        // GH-4288. A durable database-backed queue moves each envelope into the incoming
        // (inbox) table as part of the dequeue itself, so the companion local queue's
        // DurableReceiver must not store it a second time -- that threw
        // DuplicateIncomingEnvelopeException on every message and parked all of them in the
        // inbox as permanently stuck 'Incoming' rows owned by a live node.
        if (_envelopesArePersistedInInbox)
        {
            envelope.WasPersistedInInbox = true;
        }

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
