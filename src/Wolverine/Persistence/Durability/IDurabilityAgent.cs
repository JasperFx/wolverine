using System.Threading.Tasks;

namespace Wolverine.Persistence.Durability;

public interface IDurabilityAgent
{
    void EnqueueLocally(Envelope envelope);
    void RescheduleIncomingRecovery();
    void RescheduleOutgoingRecovery();
}
