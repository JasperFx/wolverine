using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

internal interface ILocalQueue : IReceiver
{
    void Enqueue(Envelope envelope);
    int QueueCount { get; }
}
