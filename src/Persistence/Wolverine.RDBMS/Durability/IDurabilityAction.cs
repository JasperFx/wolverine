using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

[Obsolete("Going away after durability agent transition to new agent model")]
internal interface IDurabilityAction
{
    string Description { get; }
    Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent, IDurableStorageSession session);
}