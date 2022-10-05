using System.Threading.Tasks;

namespace Wolverine.Persistence.Durability;

internal interface IMessagingAction
{
    string Description { get; }
    Task ExecuteAsync(IEnvelopePersistence storage, IDurabilityAgent agent);
}
