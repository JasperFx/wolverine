using System.Data.Common;
using JasperFx.Core;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using CommandExtensions = Weasel.Core.CommandExtensions;

namespace Wolverine.Postgresql;

internal class PostgresqlNodePersistence : INodeAgentPersistence
{
    public static int LeaderLockId = 9999999;
    private readonly DbObjectName _assignmentTable;
    private readonly DbObjectName _nodeTable;

    private readonly DatabaseSettings _settings;

    public PostgresqlNodePersistence(DatabaseSettings settings)
    {
        _settings = settings;
        _nodeTable = new DbObjectName(settings.SchemaName ?? "public", DatabaseConstants.NodeTableName);
        _assignmentTable = new DbObjectName(settings.SchemaName ?? "public", DatabaseConstants.NodeAssignmentsTableName);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = (NpgsqlCommand)CommandExtensions.CreateCommand(conn,
                $"insert into {_nodeTable} (id, uri, capabilities, description) values (:id, :uri, :capabilities, :description) returning node_number")
            .With("id", node.Id)
            .With("uri", node.ControlUri.ToString())
            .With("description", node.Description);

        var strings = node.Capabilities.Select(x => x.ToString()).ToArray();
        CommandExtensions.With(cmd, "capabilities", strings);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);

        await conn.CloseAsync();

        return (int)raw;
    }

    public async Task DeleteAsync(Guid nodeId)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await CommandExtensions.CreateCommand(conn, $"delete from {_nodeTable} where id = :id")
            .With("id", nodeId)
            .ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();

        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = CommandExtensions.CreateCommand(conn,
            $"select id, node_number, description, uri, started, capabilities from {_nodeTable};select id, node_id, started from {_assignmentTable};");

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var node = await readNode(reader);
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

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var cmd = CommandExtensions.CreateCommand(conn,
            $"select id, node_number, description, uri, started, capabilities from {_nodeTable} where id = :id;select id, node_id, started from {_assignmentTable} where node_id = :id;")
            .With("id", nodeId);

        WolverineNode returnValue = null;
        
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            returnValue = await readNode(reader);
            
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
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand($"update {_nodeTable} set health_check = now() where id = :id")
            .With("id", nodeId).ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var builder = new CommandBuilder();
        var nodeParameter = builder.AddNamedParameter("node", nodeId, NpgsqlDbType.Uuid);

        foreach (var agent in agents)
        {
            var parameter = builder.AddParameter(agent.ToString());
            builder.Append(
                $"insert into {_assignmentTable} (id, node_id) values (:{parameter.ParameterName}, :{nodeParameter.ParameterName}) on conflict (id) do update set node_id = :{nodeParameter.ParameterName};");
        }

        await builder.ExecuteNonQueryAsync(conn, cancellationToken);


        await conn.CloseAsync();
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.CreateCommand($"delete from {_assignmentTable} where id = :id and node_id = :node")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
        
        await conn.CloseAsync();
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.CreateCommand($"insert into {_assignmentTable} (id, node_id) values (:id, :node) on conflict (id) do update set node_id = :node;")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
        
        await conn.CloseAsync();
    }

    public async Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var lockResult = await conn.TryGetGlobalLock(LeaderLockId);
        if (lockResult == AttainLockResult.Success)
        {
            var present = await currentLeaderAsync(conn);
            if (present != originalLeader)
            {
                await conn.CloseAsync();
                return present;
            }

            try
            {
                await conn.CreateCommand(
                        $"insert into {_assignmentTable} (id, node_id) values (:id, :node) on conflict (id) do update set node_id = :node;")
                    .With("id", NodeAgentController.LeaderUri.ToString())
                    .With("node", id)
                    .ExecuteNonQueryAsync();
            }
            catch (NpgsqlException e)
            {
                if (e.Message.Contains("violates foreign key constraint \"fkey_wolverine_node_assignments_node_id\""))
                {
                    return null;
                }
                
                throw;
            }


            await conn.CloseAsync();

            return id;
        }

        var leader = await currentLeaderAsync(conn);
        await conn.CloseAsync();

        return leader;
    }

    public async Task<Uri?> FindLeaderControlUriAsync(Guid selfId)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var raw = await conn.CreateCommand($"select uri from {_nodeTable} inner join {_assignmentTable} on {_nodeTable}.id = {_assignmentTable}.node_id where {_assignmentTable}.id = :id").With("id", NodeAgentController.LeaderUri.ToString())
            .ExecuteScalarAsync();

        await conn.CloseAsync();

        return raw == null ? null : new Uri((string)raw);
    }
    
    public async Task<IReadOnlyList<Uri>> LoadAllOtherNodeControlUrisAsync(Guid selfId)
    {
        await using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var list = await conn.CreateCommand($"select uri from {_nodeTable} where id != :id").With("id", selfId)
            .FetchListAsync<string>();

        await conn.CloseAsync();

        return list.Select(x => x.ToUri()).ToList();
    }

    private async Task<WolverineNode> readNode(DbDataReader reader)
    {
        var node = new WolverineNode
        {
            Id = await reader.GetFieldValueAsync<Guid>(0),
            AssignedNodeId = await reader.GetFieldValueAsync<int>(1),
            Description = await reader.GetFieldValueAsync<string>(2),
            ControlUri = (await reader.GetFieldValueAsync<string>(3)).ToUri(),
            Started = await reader.GetFieldValueAsync<DateTimeOffset>(4)
        };

        var capabilities = await reader.GetFieldValueAsync<string[]>(5);
        node.Capabilities.AddRange(capabilities.Select(x => new Uri(x)));

        return node;
    }

    private async Task<Guid?> currentLeaderAsync(NpgsqlConnection conn)
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
}