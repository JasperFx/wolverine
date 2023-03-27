using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

internal interface IDurabilityAction
{
    string Description { get; }
    Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent, IDurableStorageSession session);
}