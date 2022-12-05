using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

internal interface ILocalQueue : IReceiver
{
    int QueueCount { get; }
    void Enqueue(Envelope envelope);
}