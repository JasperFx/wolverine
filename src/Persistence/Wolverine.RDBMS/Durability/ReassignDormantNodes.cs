using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

public class ReassignDormantNodes : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly ILogger _logger;
    private readonly INodeAgentPersistence _nodes;
    private readonly List<int> _owners = new();

    public ReassignDormantNodes(INodeAgentPersistence nodes, IMessageDatabase database, ILogger logger)
    {
        _nodes = nodes;
        _database = database;
        _logger = logger;
    }

    public string Description { get; } = "Reassigning persisted messages from obsolete nodes";

    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        var sql =
            $"select distinct owner_id from {_database.SchemaName}.{DatabaseConstants.IncomingTable} union select distinct owner_id from {_database.SchemaName}.{DatabaseConstants.OutgoingTable};";

        await using var conn = _database.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        var owners = await conn.CreateCommand(sql).FetchListAsync<int>(cancellationToken);
        await conn.CloseAsync();

        var nodes = await _nodes.LoadAllNodeAssignedIdsAsync();

        var dormant = owners.Where(x => !nodes.Contains(x));
        foreach (var owner in dormant) yield return new NodeRecoveryOperation(owner);
    }
}