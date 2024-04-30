using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using CommandExtensions = Weasel.Core.CommandExtensions;

namespace Wolverine.SqlServer.Persistence;

internal class SqlServerNodePersistence : DatabaseConstants, INodeAgentPersistence
{
    public static string LeaderLockId = "9999999";
    
    private readonly DatabaseSettings _settings;
    private readonly IMessageDatabase _database;
    private readonly DbObjectName _nodeTable;
    private readonly DbObjectName _assignmentTable;

    public SqlServerNodePersistence(DatabaseSettings settings, IMessageDatabase database)
    {
        _settings = settings;
        _database = database;
        _nodeTable = new DbObjectName(settings.SchemaName ?? "dbo", DatabaseConstants.NodeTableName);
        _assignmentTable = new DbObjectName(settings.SchemaName ?? "dbo", DatabaseConstants.NodeAssignmentsTableName);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.CreateCommand($"delete from {_nodeTable}").ExecuteNonQueryAsync(cancellationToken);
        
        await conn.CloseAsync();
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var strings = node.Capabilities.Select(x => x.ToString()).Join(",");
        
        var cmd = conn.CreateCommand($"insert into {_nodeTable} (id, uri, capabilities, description) OUTPUT Inserted.node_number values (@id, @uri, @capabilities, @description) ")
            .With("id", node.Id)
            .With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString()).With("description", node.Description)
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

        var cmd = conn.CreateCommand($"select {NodeColumns} from {_nodeTable};select {Id}, {NodeId}, {Started} from {_assignmentTable}");

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

        await reader.CloseAsync();
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
                $"select id, node_number, description, uri, started, health_check, capabilities from {_nodeTable} where id = @id;select id, node_id, started from {_assignmentTable} where node_id = @id;")
            .With("id", nodeId);

        WolverineNode returnValue = default!;
        
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

        await reader.CloseAsync();

        return returnValue;
    }

    public async Task MarkHealthCheckAsync(Guid nodeId)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand($"update {_nodeTable} set health_check = GETUTCDATE() where id = @id")
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
            Started = await reader.GetFieldValueAsync<DateTimeOffset>(4),
            LastHealthCheck = await reader.GetFieldValueAsync<DateTimeOffset>(5)
        };

        var capabilities = await reader.GetFieldValueAsync<string>(6);
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

        var command = builder.Compile();
        command.Connection = conn;
        await command.ExecuteNonQueryAsync(cancellationToken);


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

        return list.Select(x => x!.ToUri()).ToList();
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
    
    public Task LogRecordsAsync(params NodeRecord[] records)
    {
        if (records.Any())
        {
            var op = new PersistNodeRecord(_settings, records);
            return _database.EnqueueAsync(op);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be a positive number");

        Func<DbDataReader, Task<NodeRecord>> readRecord = async reader =>
        {
            return new NodeRecord
            {
                NodeNumber = await reader.GetFieldValueAsync<int>(0),
                RecordType = Enum.Parse<NodeRecordType>(await reader.GetFieldValueAsync<string>(1)),
                Timestamp = await reader.GetFieldValueAsync<DateTimeOffset>(2),
                Description = await reader.GetFieldValueAsync<string>(3)

            };
        };
        
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        var result = await conn.CreateCommand($"select top {count} node_number, event_name, timestamp, description from {_settings.SchemaName}.{DatabaseConstants.NodeRecordTableName} order by id desc")
            .FetchListAsync(readRecord);

        await conn.CloseAsync();

        return result;
    }
}
