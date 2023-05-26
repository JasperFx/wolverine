using System.Data.Common;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class NodeRecoveryOperation : IDatabaseOperation
{
    private readonly IMessageDatabase _database;
    private readonly int _ownerNodeId;

    public NodeRecoveryOperation(IMessageDatabase database, int ownerNodeId)
    {
        _database = database;
        _ownerNodeId = ownerNodeId;
    }

    public string Description => $"Reassign inbox/outbox messages originally owned by node {_ownerNodeId} to any node";
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        var ownerParameter = builder.AddParameter(_ownerNodeId);
        
        builder.Append($"update {_database.SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @{ownerParameter.ParameterName};");
        builder.Append($"update {_database.SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = 0 where owner_id = @{ownerParameter.ParameterName};");
        
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        var inboxNumber = reader.RecordsAffected;
        
        await reader.NextResultAsync(token);

        var outboxNumber = reader.RecordsAffected;
        
        // TODO -- definitely do something with logging and the tracker here to support testing
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}