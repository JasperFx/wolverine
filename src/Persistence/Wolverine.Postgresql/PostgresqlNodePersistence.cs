using System.Data.Common;
using JasperFx.Core;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Postgresql;

internal class PostgresqlNodePersistence : DatabaseConstants, INodeAgentPersistence
{
    public static int LeaderLockId = 9999999;
    private readonly DbObjectName _assignmentTable;
    private readonly DbObjectName _nodeTable;

    private readonly DatabaseSettings _settings;
    private readonly IMessageDatabase _database;
    private readonly NpgsqlDataSource _dataSource;

    public PostgresqlNodePersistence(DatabaseSettings settings, PostgresqlMessageStore database,
        NpgsqlDataSource dataSource)
    {
        _settings = settings;
        _database = database;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _nodeTable = new DbObjectName(settings.SchemaName ?? "public", DatabaseConstants.NodeTableName);
        _assignmentTable =
            new DbObjectName(settings.SchemaName ?? "public", DatabaseConstants.NodeAssignmentsTableName);
    }

    public Task ClearAllAsync(CancellationToken cancellationToken)
    {
        return _dataSource.CreateCommand($"delete from {_nodeTable}")
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var cmd = (NpgsqlCommand)_dataSource.CreateCommand(
                $"insert into {_nodeTable} (id, uri, capabilities, description) values (:id, :uri, :capabilities, :description) returning node_number")
            .With("id", node.Id)
            .With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString())
            .With("description", node.Description);

        var strings = node.Capabilities.Select(x => x.ToString()).ToArray();
        cmd.With("capabilities", strings);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);

        return (int)raw!;
    }

    public Task DeleteAsync(Guid nodeId)
    {
        if (_database.HasDisposed) return Task.CompletedTask;

        return _dataSource.CreateCommand($"delete from {_nodeTable} where id = :id")
            .With("id", nodeId)
            .ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();

        await using var cmd = _dataSource.CreateCommand(
            $"select {NodeColumns} from {_nodeTable};select {Id}, {NodeId}, {Started} from {_assignmentTable};");

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

        return nodes;
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        if (_database.HasDisposed) return null;

        await using var cmd = _dataSource.CreateCommand(
                $"select {NodeColumns} from {_nodeTable} where id = :id;select {Id}, {NodeId}, {Started} from {_assignmentTable} where node_id = :id;")
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

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var builder = new CommandBuilder();
        var nodeParameter = builder.AddNamedParameter("node", nodeId, NpgsqlDbType.Uuid);

        foreach (var agent in agents)
        {
            var parameter = builder.AddParameter(agent.ToString());
            builder.Append(
                $"insert into {_assignmentTable} (id, node_id) values (:{parameter.ParameterName}, :{nodeParameter.ParameterName}) on conflict (id) do update set node_id = :{nodeParameter.ParameterName};");
        }

        var command = builder.Compile();
        command.Connection = conn;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await conn.CloseAsync();
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await _dataSource.CreateCommand($"delete from {_assignmentTable} where id = :id and node_id = :node")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await _dataSource.CreateCommand(
                $"insert into {_assignmentTable} (id, node_id) values (:id, :node) on conflict (id) do update set node_id = :node;")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid?> MarkNodeAsLeaderAsync(Guid? originalLeader, Guid id)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();

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
            finally
            {
                await conn.ReleaseGlobalLock(LeaderLockId);
                await conn.CloseAsync();
            }

            return id;
        }

        var leader = await currentLeaderAsync(conn);
        await conn.CloseAsync();

        return leader;
    }

    public async Task<Uri?> FindLeaderControlUriAsync(Guid selfId)
    {
        if (_database.HasDisposed) return null;

        var raw = await _dataSource
            .CreateCommand(
                $"select uri from {_nodeTable} inner join {_assignmentTable} on {_nodeTable}.id = {_assignmentTable}.node_id where {_assignmentTable}.id = :id")
            .With("id", NodeAgentController.LeaderUri.ToString())
            .ExecuteScalarAsync();

        return raw == null ? null : new Uri((string)raw);
    }

    public async Task<IReadOnlyList<Uri>> LoadAllOtherNodeControlUrisAsync(Guid selfId)
    {
        var list = await _dataSource.CreateCommand($"select uri from {_nodeTable} where id != :id").With("id", selfId)
            .FetchListAsync<string>();

        return list.Select(x => x!.ToUri()).ToList();
    }

    [Obsolete("Will be removed in Wolverine 3.0")]
    public async Task<IReadOnlyList<WolverineNode>> LoadAllStaleNodesAsync(DateTimeOffset staleTime,
        CancellationToken cancellationToken)
    {
        var cmd = _dataSource
            .CreateCommand($"select id, uri from {_nodeTable} where health_check < :stale")
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
        }, cancellationToken);

        return nodes;
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await _dataSource.CreateCommand($"update {_nodeTable} set health_check = :now where id = :id")
            .With("id", nodeId)
            .With("now", lastHeartbeatTime)
            .ExecuteNonQueryAsync();
    }

    public async Task MarkHealthCheckAsync(Guid nodeId)
    {
        await _dataSource.CreateCommand($"update {_nodeTable} set health_check = now() where id = :id")
            .With("id", nodeId).ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<int>> LoadAllNodeAssignedIdsAsync()
    {
        return await _dataSource.CreateCommand($"select node_number from {_nodeTable}")
            .FetchListAsync<int>();
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
            LastHealthCheck = await reader.GetFieldValueAsync<DateTimeOffset>(5),
        };

        var capabilities = await reader.GetFieldValueAsync<string[]>(6);
        node.Capabilities.AddRange(capabilities.Select(x => new Uri(x)));

        return node;
    }

    private async Task<Guid?> currentLeaderAsync(NpgsqlConnection conn)
    {
        var current = await _dataSource
            .CreateCommand(
                $"select node_id from {_assignmentTable} where id = '{NodeAgentController.LeaderUri}'")
            .ExecuteScalarAsync();

        if (current is Guid nodeId)
        {
            return nodeId;
        }

        return null;
    }

    public Task LogRecordsAsync(params NodeRecord[] records)
    {
        if (records.Length == 0) return Task.CompletedTask;

        var op = new PersistNodeRecord(_settings, records);
        return _database.EnqueueAsync(op);
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

        return await _dataSource.CreateCommand($"select node_number, event_name, timestamp, description from {_settings.SchemaName}.{DatabaseConstants.NodeRecordTableName} order by id desc LIMIT :limit")
            .With("limit", count)
            .FetchListAsync(readRecord);
    }
}