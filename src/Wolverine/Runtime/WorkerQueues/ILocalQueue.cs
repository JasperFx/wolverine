using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

public interface ILocalQueue : IReceiver
{
    void Enqueue(Envelope envelope);
}
