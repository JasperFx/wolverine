using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

public interface ILocalReceiver
{
    void Enqueue(Envelope envelope);

    ValueTask EnqueueAsync(Envelope envelope);
}

// GH-4186: QueueCount now comes from IHasQueueDepth, which the receivers that are NOT local queues
// (InlineReceiver, NativeAckReceiver) can implement without also claiming they can be enqueued into.
public interface ILocalQueue : IReceiver, ILocalReceiver, IHasQueueDepth
{
    Uri Uri { get; }
}