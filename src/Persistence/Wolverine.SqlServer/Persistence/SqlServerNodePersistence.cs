using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using CommandExtensions = Weasel.Core.CommandExtensions;

namespace Wolverine.SqlServer.Persistence;

internal class SqlServerNodePersistence : INodeAgentPersistence
{
    public static string LeaderLockId = "9999999";
    
    private readonly DatabaseSettings _settings;
    private readonly DbObjectName _nodeTable;
    private readonly DbObjectName _assignmentTable;

    public SqlServerNodePersistence(DatabaseSettings settings)
    {
        _settings = settings;        
        _nodeTable = new DbObjectName(settings.SchemaName ?? "dbo", DatabaseConstants.NodeTableName);
        _assignmentTable = new DbObjectName(settings.SchemaName ?? "dbo", DatabaseConstants.NodeAssignmentsTableName);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var strings = node.Capabilities.Select(x => x.ToString()).Join(",");
        
        var cmd = (SqlCommand)conn.CreateCommand($"insert into {_nodeTable} (id, uri, capabilities, description) OUTPUT Inserted.node_number values (@id, @uri, @capabilities, @description) ")
            .With("id", node.Id)
            .With("uri", node.ControlUri.ToString()).With("description", node.Description)
            .With("capabilities", strings);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);

        await conn.CloseAsync();

        return (int)raw;
    }

    public async Task DeleteAsync(Guid nodeId)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await CommandExtensions.CreateCommand(conn, $"delete from {_nodeTable} where id = @id")
            .With("id", nodeId)
            .ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();
        
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = conn.CreateCommand($"select id, node_number, description, uri, started, capabilities from {_nodeTable};select id, node_id, started from {_assignmentTable}");

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var node = await readNodeAsync(reader);
            nodes.Add(node);
        }

        var dict = nodes.ToDictionary(x => x.Id);

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var agentId = new Uri(await reader.GetFieldValueAsync<string>(0, cancellationToken));
            var nodeId = await reader.GetFieldValueAsync<Guid>(1, cancellationToken);
            
            dict[nodeId].ActiveAgents.Add(agentId);
        }

        await conn.CloseAsync();

        return nodes;
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllStaleNodesAsync(DateTimeOffset staleTime, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = conn
            .CreateCommand($"select id, uri from {_nodeTable} where health_check < @stale")
            .With("stale", staleTime);
        var nodes = await cmd.FetchListAsync<WolverineNode>(async reader =>
        {
            var id = await reader.GetFieldValueAsync<Guid>(0, cancellationToken);
            var raw = await reader.GetFieldValueAsync<string>(1, cancellationToken);

            return new WolverineNode
            {
                Id = id,
                ControlUri = new Uri(raw)
            };
        }, cancellation: cancellationToken);

        await conn.CloseAsync();

        return nodes;
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand($"update {_nodeTable} set health_check = @now where id = @id")
            .With("id", nodeId)
            .With("now", lastHeartbeatTime)
            .ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = CommandExtensions.CreateCommand(conn,
                $"select id, node_number, description, uri, started, capabilities from {_nodeTable} where id = @id;select id, node_id, started from {_assignmentTable} where node_id = @id;")
            .With("id", nodeId);

        WolverineNode returnValue = null;
        
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            returnValue = await readNodeAsync(reader);
            
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var agentId = new Uri(await reader.GetFieldValueAsync<string>(0, cancellationToken));
                returnValue.ActiveAgents.Add(agentId);
            }
        }

        return returnValue;
    }

    // TODO -- unit test this
    public async Task MarkHealthCheckAsync(Guid nodeId)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand($"update {_nodeTable} set health_check = GETDATE() where id = @id")
            .With("id", nodeId).ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    private async Task<WolverineNode> readNodeAsync(DbDataReader reader)
    {
        var node = new WolverineNode
        {
            Id = await reader.GetFieldValueAsync<Guid>(0),
            AssignedNodeId = await reader.GetFieldValueAsync<int>(1),
            Description = await reader.GetFieldValueAsync<string>(2),
            ControlUri = (await reader.GetFieldValueAsync<string>(3)).ToUri(),
            Started = await reader.GetFieldValueAsync<DateTimeOffset>(4)
        };

        var capabilities = await reader.GetFieldValueAsync<string>(5);
        if (capabilities.IsNotEmpty())
        {
            node.Capabilities.AddRange(capabilities.Split(',').Select(x => new Uri(x)));
        }

        return node;
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var builder = new CommandBuilder();
        var nodeParameter = builder.AddNamedParameter("node", nodeId, SqlDbType.UniqueIdentifier);
        
        foreach (var agent in agents)
        {
            var parameter = builder.AddParameter(agent.ToString());
            builder.Append($"delete from {_assignmentTable} where id = @{parameter.ParameterName};insert into {_assignmentTable} (id, node_id) values (@{parameter.ParameterName}, @{nodeParameter.ParameterName});");
        }

        await builder.ExecuteNonQueryAsync(conn, cancellationToken);


        await conn.CloseAsync();
    }
    
    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.CreateCommand($"delete from {_assignmentTable} where id = @id and node_id = @node")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
        
        await conn.CloseAsync();
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.CreateCommand($"delete from {_assignmentTable} where id = @id;insert into {_assignmentTable} (id, node_id) values (@id, @node);")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
        
        await conn.CloseAsync();
    }

    public async Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();


        var lockResult = await conn.TryGetGlobalLock(LeaderLockId);
        if (lockResult)
        {
            var present = await currentLeaderAsync(conn);
            if (present != originalLeader)
            {
                await conn.CloseAsync();
                return present;
            }

            await conn.CreateCommand(
                    $"delete from {_assignmentTable} where id = @id;insert into {_assignmentTable} (id, node_id) values (@id, @node);")
                .With("id", NodeAgentController.LeaderUri.ToString())
                .With("node", id)
                .ExecuteNonQueryAsync();


            await conn.CloseAsync();

            return id;
        }

        var leader = await currentLeaderAsync(conn);
        await conn.CloseAsync();

        return leader;
    }

    public async Task<Uri?> FindLeaderControlUriAsync(Guid selfId)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var raw = await conn.CreateCommand($"select uri from {_nodeTable} inner join {_assignmentTable} on {_nodeTable}.id = {_assignmentTable}.node_id where {_assignmentTable}.id = @id").With("id", NodeAgentController.LeaderUri.ToString())
            .ExecuteScalarAsync();

        await conn.CloseAsync();

        return raw == null ? null : new Uri((string)raw);
    }

    private async Task<Guid?> currentLeaderAsync(SqlConnection conn)
    {
        var current = await conn
            .CreateCommand(
                $"select node_id from {_assignmentTable} where id = '{NodeAgentController.LeaderUri}'")
            .ExecuteScalarAsync();

        if (current is Guid nodeId)
        {
            return nodeId;
        }

        return null;
    }
    
    public async Task<IReadOnlyList<Uri>> LoadAllOtherNodeControlUrisAsync(Guid selfId)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var list = await conn.CreateCommand($"select uri from {_nodeTable} where id != @id").With("id", selfId)
            .FetchListAsync<string>();

        await conn.CloseAsync();

        return list.Select(x => x.ToUri()).ToList();
    }
    
    public async Task<IReadOnlyList<int>> LoadAllNodeAssignedIdsAsync()
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var result = await conn.CreateCommand($"select node_number from {_nodeTable}")
            .FetchListAsync<int>();

        await conn.CloseAsync();

        return result;
    }
}