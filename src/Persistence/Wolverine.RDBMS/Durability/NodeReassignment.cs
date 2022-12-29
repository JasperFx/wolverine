using System.Linq;
using System.Threading.Tasks;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

internal class NodeReassignment : IDurabilityAction
{
    public string Description { get; } = "Dormant node reassignment";

    public async Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent, NodeSettings nodeSettings,
        DatabaseSettings databaseSettings)
    {
        await storage.Session.WithinTransactionalGlobalLockAsync(TransportConstants.ReassignmentLockId,
            () => ReassignNodesAsync(storage, nodeSettings));
    }

    public static async Task ReassignNodesAsync(IMessageStore storage, NodeSettings nodeSettings)
    {
        var owners = await storage.FindUniqueOwnersAsync(nodeSettings.UniqueNodeId);

        foreach (var owner in owners.Where(x => x != TransportConstants.AnyNode))
        {
            if (owner == nodeSettings.UniqueNodeId)
            {
                continue;
            }

            if (await storage.Session.TryGetGlobalTxLockAsync(owner))
            {
                await storage.ReassignDormantNodeToAnyNodeAsync(owner);
            }
        }
    }
}