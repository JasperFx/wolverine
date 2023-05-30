using System.Data.Common;
using JasperFx.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

// TODO -- not going to work when we go multi-tenanted!
public class ReassignDormantNodes : IDatabaseOperation
{
    private readonly IMessageDatabase _database;
    private List<int> _obsoletes;

    public ReassignDormantNodes(IMessageDatabase database)
    {
        _database = database;
    }

    public string Description { get; } = "Reassigning persisted messages from obsolete nodes";
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append($"select distinct owner_id from {_database.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id not in (select node_number from {_database.SchemaName}.{DatabaseConstants.NodeTableName});");
        var outgoingSql = $"select distinct owner_id from {_database.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id not in (select node_number from {_database.SchemaName}.{DatabaseConstants.NodeTableName});";
        builder.Append(outgoingSql);
    }

    public async Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        _obsoletes = new List<int>();

        while (await reader.ReadAsync(token))
        {
            var ownerId = await reader.GetFieldValueAsync<int>(0, token);
            _obsoletes.Fill(ownerId);
        }

        await reader.NextResultAsync(token);
        
        while (await reader.ReadAsync(token))
        {
            var ownerId = await reader.GetFieldValueAsync<int>(0, token);
            _obsoletes.Fill(ownerId);
        }
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        return _obsoletes.Select(id => new NodeRecoveryOperation(id));
    }
}