using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

public interface ILocalReceiver
{
    void Enqueue(Envelope envelope);

    ValueTask EnqueueAsync(Envelope envelope);
}

public interface ILocalQueue : IReceiver, ILocalReceiver
{
    int QueueCount { get; }
    Uri Uri { get; }
}