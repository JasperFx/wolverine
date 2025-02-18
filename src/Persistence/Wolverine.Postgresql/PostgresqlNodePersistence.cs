using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
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
    private readonly IMessageDatabase _database;
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _lockId;
    private readonly DbObjectName _nodeTable;

    private readonly DatabaseSettings _settings;

    public PostgresqlNodePersistence(DatabaseSettings settings, PostgresqlMessageStore database,
        NpgsqlDataSource dataSource)
    {
        _settings = settings;
        _database = database;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schemaName = settings.SchemaName ?? "public";
        _nodeTable = new DbObjectName(schemaName, NodeTableName);
        _assignmentTable =
            new DbObjectName(schemaName, NodeAssignmentsTableName);

        _lockId = schemaName.GetDeterministicHashCode();
    }

    public Task ClearAllAsync(CancellationToken cancellationToken)
    {
        return _dataSource.CreateCommand($"delete from {_nodeTable}")
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var cmd = _dataSource.CreateCommand(
                $"insert into {_nodeTable} (id, uri, capabilities, description, version) values (:id, :uri, :capabilities, :description, :version) returning node_number")
            .With("id", node.NodeId)
            .With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString())
            .With("description", node.Description)
            .With("version", node.Version.ToString());

        var strings = node.Capabilities.Select(x => x.ToString()).ToArray();
        cmd.With("capabilities", strings);

        var raw = await cmd.ExecuteScalarAsync(cancellationToken);

        return (int)raw!;
    }

    public Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        if (_database.HasDisposed)
        {
            return Task.CompletedTask;
        }

        return _dataSource.CreateCommand(
                $"delete from {_nodeTable} where id = :id;update {_settings.SchemaName}.{IncomingTable} set {OwnerId} = 0 where {OwnerId} = :number;update {_settings.SchemaName}.{OutgoingTable} set {OwnerId} = 0 where {OwnerId} = :number;")
            .With("id", nodeId)
            .With("number", assignedNodeNumber)
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

        var dict = nodes.ToDictionary(x => x.NodeId);

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
        if (_database.HasDisposed)
        {
            return null;
        }

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

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await _dataSource.CreateCommand($"update {_nodeTable} set health_check = :now where id = :id")
            .With("id", nodeId)
            .With("now", lastHeartbeatTime)
            .ExecuteNonQueryAsync();
    }

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken token)
    {
        var count = await _dataSource.CreateCommand($"update {_nodeTable} set health_check = now() where id = :id")
            .With("id", node.NodeId).ExecuteNonQueryAsync(token);

        if (count == 0)
        {
            await PersistAsync(node, token);
        }
    }

    public Task LogRecordsAsync(params NodeRecord[] records)
    {
        if (records.Length == 0)
        {
            return Task.CompletedTask;
        }

        var op = new PersistNodeRecord(_settings, records);
        return _database.EnqueueAsync(op);
    }

    public async Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Must be a positive number");
        }

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

        return await _dataSource
            .CreateCommand(
                $"select node_number, event_name, timestamp, description from {_settings.SchemaName}.{NodeRecordTableName} order by id desc LIMIT :limit")
            .With("limit", count)
            .FetchListAsync(readRecord);
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

        if (!(await reader.IsDBNullAsync(6)))
        {
            var rawVersion = await reader.GetFieldValueAsync<string>(6);
            node.Version = System.Version.Parse(rawVersion);
        }

        var capabilities = await reader.GetFieldValueAsync<string[]>(7);
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
}

internal class AdvisoryLock : IAdvisoryLock
{
    private readonly string _databaseName;
    private readonly List<int> _locks = new();
    private readonly ILogger _logger;
    private readonly NpgsqlDataSource _source;
    private NpgsqlConnection _conn;

    public AdvisoryLock(NpgsqlDataSource source, ILogger logger, string databaseName)
    {
        _source = source;
        _logger = logger;
        _databaseName = databaseName;
    }

    public bool HasLock(int lockId)
    {
        return _conn is not { State: ConnectionState.Closed } && _locks.Contains(lockId);
    }

    public async Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
    {
        if (_conn == null)
        {
            _conn = _source.CreateConnection();
            await _conn.OpenAsync(token).ConfigureAwait(false);
        }

        if (_conn.State == ConnectionState.Closed)
        {
            try
            {
                await _conn.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to clean up and restart an advisory lock connection");
            }
            finally
            {
                _conn = null;
            }

            return false;
        }


        var attained = await _conn.TryGetGlobalLock(lockId, token).ConfigureAwait(false);
        if (attained == AttainLockResult.Success)
        {
            _locks.Add(lockId);
            return true;
        }

        return false;
    }

    public async Task ReleaseLockAsync(int lockId)
    {
        if (!_locks.Contains(lockId))
        {
            return;
        }

        if (_conn == null || _conn.State == ConnectionState.Closed)
        {
            _locks.Remove(lockId);
            return;
        }

        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(1.Seconds());

        await _conn.ReleaseGlobalLock(lockId, cancellation.Token).ConfigureAwait(false);
        _locks.Remove(lockId);

        if (!_locks.Any())
        {
            await _conn.CloseAsync().ConfigureAwait(false);
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn == null)
        {
            return;
        }

        try
        {
            foreach (var i in _locks) await _conn.ReleaseGlobalLock(i, CancellationToken.None).ConfigureAwait(false);

            await _conn.CloseAsync().ConfigureAwait(false);
            await _conn.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to dispose of advisory locks for database {Identifier}",
                _databaseName);
        }
        finally
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
        }
    }
}