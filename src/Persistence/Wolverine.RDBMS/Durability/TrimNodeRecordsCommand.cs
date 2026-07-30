using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// GH-3701: cap the node record table at <see cref="DurabilitySettings.NodeRecordRetention"/> rows.
///
/// <see cref="DeleteOldNodeEventRecords"/> bounds the same table by age
/// (<see cref="DurabilitySettings.NodeEventRecordExpirationTime"/>, 5 days by default), which is no ceiling
/// at all when the write rate is high: one <c>AssignmentChanged</c> row per agent per assignment decision on
/// a cluster with thousands of distributed agents is millions of rows a day, and all of them are inside the
/// age window. The reporting cluster reached 36M rows / 16 GB in five days — 15 GB of heap on a diagnostic
/// table nothing on the hot path reads, plus the write and WAL load of the inserts, on the main Wolverine
/// database, while the cluster was already unhealthy for an unrelated reason.
///
/// The "keep the newest N by id" delete itself is engine-specific (LIMIT / TOP / FETCH FIRST), so it lives
/// behind <see cref="INodeAgentPersistence.DeleteOldNodeRecordsAsync"/> on the store rather than being
/// expressed as an <c>IDatabaseOperation</c> here. Stores that do not implement it inherit the interface's
/// no-op default and are simply left to the age sweep.
/// </summary>
internal class TrimNodeRecordsCommand : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly DurabilitySettings _settings;
    private readonly ILogger _logger;

    public TrimNodeRecordsCommand(IMessageDatabase database, DurabilitySettings settings, ILogger logger)
    {
        _database = database;
        _settings = settings;
        _logger = logger;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var retention = _settings.NodeRecordRetention;
        if (retention <= 0)
        {
            return AgentCommands.Empty;
        }

        try
        {
            await _database.Nodes.DeleteOldNodeRecordsAsync(retention);
        }
        catch (Exception e)
        {
            // Housekeeping must never be able to take down the durability agent's running block.
            _logger.LogError(e, "Error trying to trim the node record table in database {Database} to {Retention} rows",
                _database.Name, retention);
        }

        return AgentCommands.Empty;
    }
}
