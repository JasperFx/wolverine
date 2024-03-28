using System.Runtime.CompilerServices;
using Weasel.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

public class ReassignDormantNodes : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly INodeAgentPersistence _nodes;

    public ReassignDormantNodes(INodeAgentPersistence nodes, IMessageDatabase database)
    {
        _nodes = nodes;
        _database = database;
    }

    public string Description { get; } = "Reassigning persisted messages from obsolete nodes";

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        var sql =
            $"select distinct owner_id from {_database.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id > 0 union select distinct owner_id from {_database.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id > 0;";
        
        var owners = await _database.DataSource.CreateCommand(sql).FetchListAsync<int>(cancellationToken);

        var nodes = await _nodes.LoadAllNodeAssignedIdsAsync();

        var dormant = owners.Where(x => !nodes.Contains(x));

        var commands = new AgentCommands();
        commands.AddRange(dormant.Select(x => new NodeRecoveryOperation(x)));

        return commands;
    }
}