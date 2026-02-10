using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Weasel.Sqlite;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Sqlite;

internal class SqliteNodePersistence : DatabaseConstants, INodeAgentPersistence
{
    public static int LeaderLockId = 9999999;
    private readonly DbObjectName _assignmentTable;
    private readonly IMessageDatabase _database;
    private readonly DbDataSource _dataSource;
    private readonly int _lockId;
    private readonly DbObjectName _nodeTable;

    private readonly DatabaseSettings _settings;
    private readonly DbObjectName _restrictionTable;

    public SqliteNodePersistence(DatabaseSettings settings, SqliteMessageStore database,
        DbDataSource dataSource)
    {
        _settings = settings;
        _database = database;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schemaName = settings.SchemaName ?? "main";
        _nodeTable = new SqliteObjectName(NodeTableName);
        _restrictionTable = new SqliteObjectName(DatabaseConstants.AgentRestrictionsTableName);
        _assignmentTable = new SqliteObjectName(NodeAssignmentsTableName);

        _lockId = schemaName.GetDeterministicHashCode();
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await conn.CreateCommand($"delete from {_nodeTable}")
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        // SQLite doesn't have RETURNING clause in the same way, we need to use last_insert_rowid()
        var capabilitiesJson = System.Text.Json.JsonSerializer.Serialize(node.Capabilities.Select(x => x.ToString()).ToArray());

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var cmd = conn.CreateCommand(
                $"insert into {_nodeTable} (id, uri, capabilities, description, version, node_number) values (@id, @uri, @capabilities, @description, @version, @node_number)")
            .With("id", node.NodeId.ToString())
            .With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString())
            .With("description", node.Description)
            .With("version", node.Version.ToString())
            .With("capabilities", capabilitiesJson)
            .With("node_number", node.AssignedNodeNumber);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Get the node_number that was assigned
        var numberCmd = conn.CreateCommand($"select node_number from {_nodeTable} where id = @id")
            .With("id", node.NodeId.ToString());
        var raw = await numberCmd.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(raw);
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        if (_database.HasDisposed)
        {
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await conn.CreateCommand(
                $"delete from {_nodeTable} where id = @id;update {IncomingTable} set {OwnerId} = 0 where {OwnerId} = @number;update {OutgoingTable} set {OwnerId} = 0 where {OwnerId} = @number;")
            .With("id", nodeId.ToString())
            .With("number", assignedNodeNumber)
            .ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Execute first query for nodes
        await using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = $"select {NodeColumns} from {_nodeTable}";
        await using var reader = await cmd1.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var node = await readNodeAsync(reader);
            nodes.Add(node);
        }
        await reader.CloseAsync();

        var dict = nodes.ToDictionary(x => x.NodeId);

        // Execute second query for assignments
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = $"select {Id}, {NodeId}, {Started} from {_assignmentTable}";
        await using var reader2 = await cmd2.ExecuteReaderAsync(cancellationToken);

        while (await reader2.ReadAsync(cancellationToken))
        {
            var agentId = new Uri(await reader2.GetFieldValueAsync<string>(0, cancellationToken));
            var nodeIdStr = await reader2.GetFieldValueAsync<string>(1, cancellationToken);
            var nodeId = Guid.Parse(nodeIdStr);

            if (dict.ContainsKey(nodeId))
            {
                dict[nodeId].ActiveAgents.Add(agentId);
            }
        }
        await reader2.CloseAsync();

        await conn.CloseAsync();

        return nodes;
    }

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions,
        CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var restriction in restrictions)
        {
            if (restriction.Type == AgentRestrictionType.None)
            {
                await conn.CreateCommand($"delete from {_restrictionTable} where id = @id")
                    .With("id", restriction.Id.ToString())
                    .ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await conn.CreateCommand(
                        $"insert or replace into {_restrictionTable} (id, uri, type, node) values (@id, @uri, @type, @node)")
                    .With("id", restriction.Id.ToString())
                    .With("uri", restriction.AgentUri.ToString())
                    .With("type", restriction.Type.ToString())
                    .With("node", restriction.NodeNumber)
                    .ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await conn.CloseAsync();
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();
        var restrictions = new List<AgentRestriction>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Load nodes
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"select {NodeColumns} from {_nodeTable}";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var node = await readNodeAsync(reader);
                nodes.Add(node);
            }
        }

        var dict = nodes.ToDictionary(x => x.NodeId);

        // Load assignments
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"select {Id}, {NodeId}, {Started} from {_assignmentTable}";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var agentId = new Uri(await reader.GetFieldValueAsync<string>(0, cancellationToken));
                var nodeIdStr = await reader.GetFieldValueAsync<string>(1, cancellationToken);
                var nodeId = Guid.Parse(nodeIdStr);

                if (dict.ContainsKey(nodeId))
                {
                    dict[nodeId].ActiveAgents.Add(agentId);
                }
            }
        }

        // Load restrictions
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"select id, uri, type, node from {_restrictionTable}";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var idStr = await reader.GetFieldValueAsync<string>(0, cancellationToken);
                var id = Guid.Parse(idStr);
                var uriString = await reader.GetFieldValueAsync<string>(1, cancellationToken);
                var typeString = await reader.GetFieldValueAsync<string>(2, cancellationToken);
                var nodeNumber = await reader.GetFieldValueAsync<long>(3, cancellationToken);

                var restriction = new AgentRestriction(id, new Uri(uriString),
                    Enum.Parse<AgentRestrictionType>(typeString), (int)nodeNumber);

                restrictions.Add(restriction);
            }
        }

        await conn.CloseAsync();

        return new(nodes, new AgentRestrictions(restrictions.ToArray()));
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        if (_database.HasDisposed)
        {
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        WolverineNode? returnValue = null;

        await using (var cmd = conn.CreateCommand($"select {NodeColumns} from {_nodeTable} where id = @id")
            .With("id", nodeId.ToString()))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                returnValue = await readNodeAsync(reader);
            }
        }

        if (returnValue != null)
        {
            await using var cmd = conn.CreateCommand($"select {Id}, {NodeId}, {Started} from {_assignmentTable} where node_id = @id")
                .With("id", nodeId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var agentId = new Uri(await reader.GetFieldValueAsync<string>(0, cancellationToken));
                returnValue.ActiveAgents.Add(agentId);
            }
        }

        await conn.CloseAsync();

        return returnValue;
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var agent in agents)
        {
            await conn.CreateCommand(
                    $"insert or replace into {_assignmentTable} (id, node_id) values (@id, @node)")
                .With("id", agent.ToString())
                .With("node", nodeId.ToString())
                .ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await conn.CreateCommand($"delete from {_assignmentTable} where id = @id and node_id = @node")
            .With("id", agentUri.ToString())
            .With("node", nodeId.ToString())
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await conn.CreateCommand(
                $"insert or replace into {_assignmentTable} (id, node_id) values (@id, @node)")
            .With("id", agentUri.ToString())
            .With("node", nodeId.ToString())
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await conn.CreateCommand($"update {_nodeTable} set health_check = @now where id = @id")
            .With("id", nodeId.ToString())
            .With("now", lastHeartbeatTime.ToString("O"))
            .ExecuteNonQueryAsync();
    }

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        var count = await conn.CreateCommand($"update {_nodeTable} set health_check = datetime('now') where id = @id")
            .With("id", node.NodeId.ToString()).ExecuteNonQueryAsync(token);

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

        var records = new List<NodeRecord>();

        await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand($"select node_number, event_name, timestamp, description from {NodeRecordTableName} order by id desc LIMIT @limit")
            .With("limit", count);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new NodeRecord
            {
                NodeNumber = Convert.ToInt32(await reader.GetFieldValueAsync<long>(0)),
                RecordType = Enum.Parse<NodeRecordType>(await reader.GetFieldValueAsync<string>(1)),
                Timestamp = DateTimeOffset.Parse(await reader.GetFieldValueAsync<string>(2)),
                Description = await reader.GetFieldValueAsync<string>(3)
            });
        }

        return records;
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
        var nodeIdStr = await reader.GetFieldValueAsync<string>(0);
        var node = new WolverineNode
        {
            NodeId = Guid.Parse(nodeIdStr),
            AssignedNodeNumber = Convert.ToInt32(await reader.GetFieldValueAsync<long>(1)),
            Description = await reader.GetFieldValueAsync<string>(2),
            ControlUri = (await reader.GetFieldValueAsync<string>(3)).ToUri(),
            Started = DateTimeOffset.Parse(await reader.GetFieldValueAsync<string>(4)),
            LastHealthCheck = DateTimeOffset.Parse(await reader.GetFieldValueAsync<string>(5))
        };

        if (!(await reader.IsDBNullAsync(6)))
        {
            var rawVersion = await reader.GetFieldValueAsync<string>(6);
            node.Version = System.Version.Parse(rawVersion);
        }

        if (!(await reader.IsDBNullAsync(7)))
        {
            var capabilitiesJson = await reader.GetFieldValueAsync<string>(7);
            var capabilities = System.Text.Json.JsonSerializer.Deserialize<string[]>(capabilitiesJson);
            if (capabilities != null)
            {
                node.Capabilities.AddRange(capabilities.Select(x => new Uri(x)));
            }
        }

        return node;
    }
}
