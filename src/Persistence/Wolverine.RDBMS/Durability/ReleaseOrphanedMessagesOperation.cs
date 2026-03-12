using System.Data.Common;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// Releases inbox/outbox messages owned by nodes that no longer exist in the wolverine_nodes table.
/// Used for main databases where the nodes table is co-located.
/// </summary>
internal class ReleaseOrphanedMessagesOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly IMessageDatabase _database;

    public ReleaseOrphanedMessagesOperation(IMessageDatabase database)
    {
        _database = database;
    }

    public string Description => "Release inbox/outbox messages owned by nodes that no longer exist";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        var schemaName = _database.SchemaName;
        var incomingTable = new DbObjectName(schemaName, DatabaseConstants.IncomingTable);
        var outgoingTable = new DbObjectName(schemaName, DatabaseConstants.OutgoingTable);
        var nodesTable = new DbObjectName(schemaName, DatabaseConstants.NodeTableName);

        builder.Append(
            $"update {incomingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.OwnerId} not in (select {DatabaseConstants.NodeNumber} from {nodesTable});");
        builder.Append(
            $"update {outgoingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.OwnerId} not in (select {DatabaseConstants.NodeNumber} from {nodesTable});");
    }

    public Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}
