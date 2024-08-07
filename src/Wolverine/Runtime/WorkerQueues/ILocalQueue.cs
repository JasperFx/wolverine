using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

public interface ILocalReceiver
{
    void Enqueue(Envelope envelope);
}

public interface ILocalQueue : IReceiver, ILocalReceiver
{
    int QueueCount { get; }
}