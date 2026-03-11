using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// Releases inbox/outbox messages owned by nodes that no longer exist.
/// Used for ancillary/tenant databases that lack the wolverine_nodes table,
/// so the active node list must be provided externally.
/// </summary>
internal class ReleaseOrphanedMessagesForAncillaryOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly IMessageDatabase _database;
    private readonly IReadOnlyList<int> _activeNodeNumbers;

    public ReleaseOrphanedMessagesForAncillaryOperation(IMessageDatabase database, IReadOnlyList<int> activeNodeNumbers)
    {
        _database = database;
        _activeNodeNumbers = activeNodeNumbers;
    }

    public string Description => "Release inbox/outbox messages owned by nodes that no longer exist (ancillary database)";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        if (_activeNodeNumbers.Count == 0) return;

        var schemaName = _database.SchemaName;
        var incomingTable = new DbObjectName(schemaName, DatabaseConstants.IncomingTable);
        var outgoingTable = new DbObjectName(schemaName, DatabaseConstants.OutgoingTable);
        var nodeList = string.Join(", ", _activeNodeNumbers);

        builder.Append(
            $"update {incomingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.OwnerId} not in ({nodeList});");
        builder.Append(
            $"update {outgoingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.OwnerId} not in ({nodeList});");
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
