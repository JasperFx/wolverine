using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

public interface ILocalReceiver
{
    [Obsolete("Try to stop using and favor the async version")]
    void Enqueue(Envelope envelope);

    ValueTask EnqueueAsync(Envelope envelope);
}

public interface ILocalQueue : IReceiver, ILocalReceiver
{
    int QueueCount { get; }
    Uri Uri { get; }
}