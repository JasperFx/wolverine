using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Runtime.Partitioning;

/// <summary>
/// Bridges an external transport listener to a companion local queue for global partitioning.
/// Messages received from the external transport are forwarded to the local durable queue
/// for sequential processing by GroupId.
/// </summary>
internal class GlobalPartitionedReceiverBridge : IReceiver, IHasQueueDepth
{
    private readonly ILocalQueue _localQueue;
    private readonly bool _envelopesArePersistedInInbox;

    public GlobalPartitionedReceiverBridge(ILocalQueue localQueue, bool envelopesArePersistedInInbox = false)
    {
        _localQueue = localQueue;
        _envelopesArePersistedInInbox = envelopesArePersistedInInbox;
    }

    public IHandlerPipeline Pipeline => _localQueue.Pipeline;

    // The same reasoning as GH-4186 for the interceptor wrapper: a receiver that only hands envelopes on has to
    // hand its depth on too. The slot's ListeningAgent reads QueueCount off its receiver, which is this bridge,
    // and the slot's BackPressureAgent decides from that number whether to stop the listener popping the shard
    // queue. Reporting 0 here meant back pressure could never engage for a global partition slot, however deep
    // the companion queue got -- and the slot's inbox recovery loop sized its pages as if the queue were empty.
    public int QueueCount => _localQueue.QueueCount;

    public DateTimeOffset? LastReceivedAt => _localQueue.LastReceivedAt;

    public PartitionedLaneDepth? LaneDepth => _localQueue.LaneDepth;

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
        // Deliberately still a no-op, and no longer for the reason this comment used to give. "The local queue
        // handles its own draining" was true of the queue and false of the handoff: nothing called it, so a node
        // losing its slot kept executing the companion queue's backlog beside the new owner (GH-4777).
        //
        // The drain now lives in ListeningAgent.drainCompanionQueueAsync, which runs only on a real stop rather
        // than on every pause, and latches before draining so in-flight handlers are waited for. It stays out of
        // here because the bridge is shared by every stop path and has no way to tell which one it is on.
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        // Don't dispose the local queue - it's managed elsewhere
    }
}
