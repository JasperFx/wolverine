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
    private readonly int _lockId;

    public SqlServerNodePersistence(DatabaseSettings settings, IMessageDatabase database)
    {
        _settings = settings;
        _database = database;
        var schemaName = settings.SchemaName ?? "dbo";
        _nodeTable = new DbObjectName(schemaName, DatabaseConstants.NodeTableName);
        _assignmentTable = new DbObjectName(schemaName, DatabaseConstants.NodeAssignmentsTableName);
        _lockId = schemaName.GetDeterministicHashCode();
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

        var raw = await persistNode(conn, node, cancellationToken);

        await conn.CloseAsync();

        return (int)raw;
    }

    private async Task<object> persistNode(SqlConnection conn, WolverineNode node, CancellationToken cancellationToken)
    {
        var strings = node.Capabilities.Select(x => x.ToString()).Join(",");

        var cmd = conn.CreateCommand($"insert into {_nodeTable} (id, uri, capabilities, description) OUTPUT Inserted.node_number values (@id, @uri, @capabilities, @description) ")
            .With("id", node.NodeId)
            .With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString()).With("description", node.Description)
            .With("capabilities", strings);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);
        return raw;
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();

        await CommandExtensions.CreateCommand(conn, $"delete from {_nodeTable} where id = @id;update {_settings.SchemaName}.{IncomingTable} set {OwnerId} = 0 where {OwnerId} = @number;update {_settings.SchemaName}.{OutgoingTable} set {OwnerId} = 0 where {OwnerId} = @number;")
            .With("id", nodeId)
            .With("number", assignedNodeNumber)
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

        var dict = nodes.ToDictionary(x => x.NodeId);

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

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var count = await conn.CreateCommand($"update {_nodeTable} set health_check = GETUTCDATE() where id = @id")
            .With("id", node.NodeId).ExecuteNonQueryAsync(cancellationToken);

        if (count == 0)
        {
            await persistNode(conn, node, cancellationToken);
        }

        await conn.CloseAsync();
    }

    private async Task<WolverineNode> readNodeAsync(DbDataReader reader)
    {
        var node = new WolverineNode
        {
            NodeId = await reader.GetFieldValueAsync<Guid>(0),
            AssignedNodeNumber = await reader.GetFieldValueAsync<int>(1),
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

    public bool HasLeadershipLock()
    {
        return _database.AdvisoryLock.HasLock(_lockId);
    }

    public Task<bool> TryAttainLeadershipLockAsync(CancellationToken token)
    {
        return _database.AdvisoryLock.TryAttainLockAsync(_lockId, token);
    }

    public Task ReleaseLeadershipLockAsync()
    {
        return _database.AdvisoryLock.ReleaseLockAsync(_lockId);
    }
}
