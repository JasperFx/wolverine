using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

[Obsolete("Make this die. Clever as hell, too heavyweight")]
internal class NodeReassignment : IDurabilityAction
{
    private readonly ILogger _logger;

    public NodeReassignment(ILogger logger)
    {
        _logger = logger;
    }

    public string Description { get; } = "Dormant node reassignment";

    public async Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent,
        IDurableStorageSession session)
    {
        await session.WithinTransactionalGlobalLockAsync(TransportConstants.ReassignmentLockId,
            () => ReassignNodesAsync(session, database.Durability, database));
    }

    public async Task ReassignNodesAsync(IDurableStorageSession session, DurabilitySettings durabilitySettings,
        IMessageDatabase wolverineDatabase)
    {
        var owners = await FindUniqueOwnersAsync(session, durabilitySettings, wolverineDatabase);

        foreach (var owner in owners.Where(x => x != TransportConstants.AnyNode))
        {
            if (owner == durabilitySettings.AssignedNodeNumber)
            {
                continue;
            }

            if (await session.TryGetGlobalTxLockAsync(owner))
            {
                await ReassignDormantNodeToAnyNodeAsync(session, owner, wolverineDatabase);
                try
                {
                    await session.ReleaseGlobalLockAsync(owner);
                }
                catch (Exception e)
                {
                    // Need to swallow the exception on releasing the tx here
                    _logger.LogError(e, "Error trying to release global transaction lock for '{Owner}'", owner);
                }
            }
        }
    }

    public static Task ReassignDormantNodeToAnyNodeAsync(IDurableStorageSession session, int nodeId,
        IMessageDatabase wolverineDatabase)
    {
        var sql = $@"
update {wolverineDatabase.SchemaName}.{DatabaseConstants.IncomingTable}
  set owner_id = 0
where
  owner_id = @owner;

update {wolverineDatabase.SchemaName}.{DatabaseConstants.OutgoingTable}
  set owner_id = 0
where
  owner_id = @owner;
";

        return session.CreateCommand(sql)
            .With("owner", nodeId)
            .ExecuteNonQueryAsync(session.Cancellation);
    }

    public static async Task<int[]> FindUniqueOwnersAsync(IDurableStorageSession session,
        DurabilitySettings durabilitySettings, IMessageDatabase wolverineDatabase)
    {
        if (session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }

        var sql = $@"
select distinct owner_id from {wolverineDatabase.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id != 0 and owner_id != @owner
union
select distinct owner_id from {wolverineDatabase.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id != 0 and owner_id != @owner";

        var list = await session.Transaction.CreateCommand(sql)
            .With("owner", durabilitySettings.AssignedNodeNumber)
            .FetchListAsync<int>(session.Cancellation);

        return list.ToArray();
    }
}