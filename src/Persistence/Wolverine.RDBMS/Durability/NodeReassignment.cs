using System;
using System.Linq;
using System.Threading.Tasks;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

internal class NodeReassignment : IDurabilityAction
{
    public string Description { get; } = "Dormant node reassignment";

    public async Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent,
        IDurableStorageSession session)
    {
        await session.WithinTransactionalGlobalLockAsync(TransportConstants.ReassignmentLockId,
            () => ReassignNodesAsync(session, database.Node, database.Settings));
    }

    public static async Task ReassignNodesAsync(IDurableStorageSession session, NodeSettings nodeSettings,
        DatabaseSettings databaseSettings)
    {
        var owners = await FindUniqueOwnersAsync(session, nodeSettings, databaseSettings);

        foreach (var owner in owners.Where(x => x != TransportConstants.AnyNode))
        {
            if (owner == nodeSettings.UniqueNodeId)
            {
                continue;
            }

            if (await session.TryGetGlobalTxLockAsync(owner))
            {
                await ReassignDormantNodeToAnyNodeAsync(session, owner, databaseSettings);
                await session.ReleaseGlobalLockAsync(owner);
            }
        }
    }
    
    public static Task ReassignDormantNodeToAnyNodeAsync(IDurableStorageSession session, int nodeId, DatabaseSettings databaseSettings)
    {
        var sql = $@"
update {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable}
  set owner_id = 0
where
  owner_id = @owner;

update {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}
  set owner_id = 0
where
  owner_id = @owner;
";

        return session.CreateCommand(sql)
            .With("owner", nodeId)
            .ExecuteNonQueryAsync(session.Cancellation);
    }
    
    public static async Task<int[]> FindUniqueOwnersAsync(IDurableStorageSession session, NodeSettings nodeSettings, DatabaseSettings databaseSettings)
    {
        if (session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }

        var sql = $@"
select distinct owner_id from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id != 0 and owner_id != @owner
union
select distinct owner_id from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id != 0 and owner_id != @owner";

        var list = await session.Transaction.CreateCommand(sql)
            .With("owner", nodeSettings.UniqueNodeId)
            .FetchList<int>(session.Cancellation);

        return list.ToArray();
    }
}