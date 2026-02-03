using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.Core;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.MySql;

internal class MySqlNodePersistence : DatabaseConstants, INodeAgentPersistence
{
    public static int LeaderLockId = 9999999;
    private readonly DbObjectName _assignmentTable;
    private readonly IMessageDatabase _database;
    private readonly MySqlDataSource _dataSource;
    private readonly int _lockId;
    private readonly DbObjectName _nodeTable;

    private readonly DatabaseSettings _settings;
    private readonly DbObjectName _restrictionTable;

    public MySqlNodePersistence(DatabaseSettings settings, MySqlMessageStore database,
        MySqlDataSource dataSource)
    {
        _settings = settings;
        _database = database;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schemaName = settings.SchemaName ?? "wolverine";
        _nodeTable = new DbObjectName(schemaName, NodeTableName);
        _restrictionTable = new DbObjectName(schemaName, AgentRestrictionsTableName);
        _assignmentTable =
            new DbObjectName(schemaName, NodeAssignmentsTableName);

        _lockId = schemaName.GetDeterministicHashCode();
    }

    public Task ClearAllAsync(CancellationToken cancellationToken)
    {
        return _dataSource.CreateCommand($"DELETE FROM {_nodeTable}")
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // MySQL doesn't have RETURNING, so we need to use LAST_INSERT_ID() or a separate query
        // Since node_number is AUTO_INCREMENT, we need to handle this differently
        var capabilities = string.Join(",", node.Capabilities.Select(x => x.ToString()));

        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText =
            $"INSERT INTO {_nodeTable} (id, uri, capabilities, description, version) VALUES (@id, @uri, @capabilities, @description, @version)";
        insertCmd.Parameters.AddWithValue("@id", node.NodeId);
        insertCmd.Parameters.AddWithValue("@uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString());
        insertCmd.Parameters.AddWithValue("@capabilities", capabilities);
        insertCmd.Parameters.AddWithValue("@description", node.Description);
        insertCmd.Parameters.AddWithValue("@version", node.Version.ToString());

        await insertCmd.ExecuteNonQueryAsync(cancellationToken);

        // Get the auto-generated node_number
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = $"SELECT node_number FROM {_nodeTable} WHERE id = @id";
        selectCmd.Parameters.AddWithValue("@id", node.NodeId);
        var result = await selectCmd.ExecuteScalarAsync(cancellationToken);

        await conn.CloseAsync();

        return Convert.ToInt32(result);
    }

    public Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        if (_database.HasDisposed)
        {
            return Task.CompletedTask;
        }

        return _dataSource.CreateCommand(
                $"DELETE FROM {_nodeTable} WHERE id = @id; UPDATE {_settings.SchemaName}.{IncomingTable} SET {OwnerId} = 0 WHERE {OwnerId} = @number; UPDATE {_settings.SchemaName}.{OutgoingTable} SET {OwnerId} = 0 WHERE {OwnerId} = @number")
            .With("id", nodeId)
            .With("number", assignedNodeNumber)
            .ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Load nodes
        await using var nodeCmd = conn.CreateCommand();
        nodeCmd.CommandText = $"SELECT {NodeColumns} FROM {_nodeTable}";
        await using var nodeReader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
        while (await nodeReader.ReadAsync(cancellationToken))
        {
            var node = await readNodeAsync(nodeReader);
            nodes.Add(node);
        }

        await nodeReader.CloseAsync();

        var dict = nodes.ToDictionary(x => x.NodeId);

        // Load assignments
        await using var assignCmd = conn.CreateCommand();
        assignCmd.CommandText = $"SELECT {Id}, {NodeId}, {Started} FROM {_assignmentTable}";
        await using var assignReader = await assignCmd.ExecuteReaderAsync(cancellationToken);
        while (await assignReader.ReadAsync(cancellationToken))
        {
            var agentId = new Uri(await assignReader.GetFieldValueAsync<string>(0, cancellationToken));
            var nodeId = await assignReader.GetFieldValueAsync<Guid>(1, cancellationToken);

            if (dict.TryGetValue(nodeId, out var node))
            {
                node.ActiveAgents.Add(agentId);
            }
        }

        await assignReader.CloseAsync();
        await conn.CloseAsync();

        return nodes;
    }

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions,
        CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var restriction in restrictions)
        {
            await using var cmd = conn.CreateCommand();

            if (restriction.Type == AgentRestrictionType.None)
            {
                cmd.CommandText = $"DELETE FROM {_restrictionTable} WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", restriction.Id);
            }
            else
            {
                // MySQL uses ON DUPLICATE KEY UPDATE instead of ON CONFLICT
                cmd.CommandText =
                    $"INSERT INTO {_restrictionTable} (id, uri, type, node) VALUES (@id, @uri, @type, @node) ON DUPLICATE KEY UPDATE node = @node";
                cmd.Parameters.AddWithValue("@id", restriction.Id);
                cmd.Parameters.AddWithValue("@uri", restriction.AgentUri.ToString());
                cmd.Parameters.AddWithValue("@type", restriction.Type.ToString());
                cmd.Parameters.AddWithValue("@node", restriction.NodeNumber);
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await conn.CloseAsync();
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();
        var restrictions = new List<AgentRestriction>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Load nodes
        await using var nodeCmd = conn.CreateCommand();
        nodeCmd.CommandText = $"SELECT {NodeColumns} FROM {_nodeTable}";
        await using var nodeReader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
        while (await nodeReader.ReadAsync(cancellationToken))
        {
            var node = await readNodeAsync(nodeReader);
            nodes.Add(node);
        }

        await nodeReader.CloseAsync();

        var dict = nodes.ToDictionary(x => x.NodeId);

        // Load assignments
        await using var assignCmd = conn.CreateCommand();
        assignCmd.CommandText = $"SELECT {Id}, {NodeId}, {Started} FROM {_assignmentTable}";
        await using var assignReader = await assignCmd.ExecuteReaderAsync(cancellationToken);
        while (await assignReader.ReadAsync(cancellationToken))
        {
            var agentId = new Uri(await assignReader.GetFieldValueAsync<string>(0, cancellationToken));
            var nodeId = await assignReader.GetFieldValueAsync<Guid>(1, cancellationToken);

            if (dict.TryGetValue(nodeId, out var node))
            {
                node.ActiveAgents.Add(agentId);
            }
        }

        await assignReader.CloseAsync();

        // Load restrictions
        await using var restrictCmd = conn.CreateCommand();
        restrictCmd.CommandText = $"SELECT id, uri, type, node FROM {_restrictionTable}";
        await using var restrictReader = await restrictCmd.ExecuteReaderAsync(cancellationToken);
        while (await restrictReader.ReadAsync(cancellationToken))
        {
            var id = await restrictReader.GetFieldValueAsync<Guid>(0, cancellationToken);
            var uriString = await restrictReader.GetFieldValueAsync<string>(1, cancellationToken);
            var typeString = await restrictReader.GetFieldValueAsync<string>(2, cancellationToken);
            var nodeNumber = await restrictReader.GetFieldValueAsync<int>(3, cancellationToken);

            var restriction = new AgentRestriction(id, new Uri(uriString),
                Enum.Parse<AgentRestrictionType>(typeString), nodeNumber);

            restrictions.Add(restriction);
        }

        await restrictReader.CloseAsync();
        await conn.CloseAsync();

        return new NodeAgentState(nodes, new AgentRestrictions(restrictions.ToArray()));
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        if (_database.HasDisposed)
        {
            return null;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        WolverineNode? returnValue = null;

        // Load node
        await using var nodeCmd = conn.CreateCommand();
        nodeCmd.CommandText = $"SELECT {NodeColumns} FROM {_nodeTable} WHERE id = @id";
        nodeCmd.Parameters.AddWithValue("@id", nodeId);
        await using var nodeReader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
        if (await nodeReader.ReadAsync(cancellationToken))
        {
            returnValue = await readNodeAsync(nodeReader);
        }

        await nodeReader.CloseAsync();

        if (returnValue != null)
        {
            // Load assignments for this node
            await using var assignCmd = conn.CreateCommand();
            assignCmd.CommandText = $"SELECT {Id}, {NodeId}, {Started} FROM {_assignmentTable} WHERE node_id = @id";
            assignCmd.Parameters.AddWithValue("@id", nodeId);
            await using var assignReader = await assignCmd.ExecuteReaderAsync(cancellationToken);
            while (await assignReader.ReadAsync(cancellationToken))
            {
                var agentId = new Uri(await assignReader.GetFieldValueAsync<string>(0, cancellationToken));
                returnValue.ActiveAgents.Add(agentId);
            }

            await assignReader.CloseAsync();
        }

        await conn.CloseAsync();

        return returnValue;
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var agent in agents)
        {
            await using var cmd = conn.CreateCommand();
            // MySQL uses ON DUPLICATE KEY UPDATE
            cmd.CommandText =
                $"INSERT INTO {_assignmentTable} (id, node_id) VALUES (@id, @node) ON DUPLICATE KEY UPDATE node_id = @node";
            cmd.Parameters.AddWithValue("@id", agent.ToString());
            cmd.Parameters.AddWithValue("@node", nodeId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await conn.CloseAsync();
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await _dataSource.CreateCommand($"DELETE FROM {_assignmentTable} WHERE id = @id AND node_id = @node")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await _dataSource.CreateCommand(
                $"INSERT INTO {_assignmentTable} (id, node_id) VALUES (@id, @node) ON DUPLICATE KEY UPDATE node_id = @node")
            .With("id", agentUri.ToString())
            .With("node", nodeId)
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await _dataSource.CreateCommand($"UPDATE {_nodeTable} SET health_check = @now WHERE id = @id")
            .With("id", nodeId)
            .With("now", lastHeartbeatTime)
            .ExecuteNonQueryAsync();
    }

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken token)
    {
        var count = await _dataSource
            .CreateCommand($"UPDATE {_nodeTable} SET health_check = UTC_TIMESTAMP(6) WHERE id = @id")
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
                $"SELECT node_number, event_name, timestamp, description FROM {_settings.SchemaName}.{NodeRecordTableName} ORDER BY id DESC LIMIT @limit")
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

        if (!await reader.IsDBNullAsync(6))
        {
            var rawVersion = await reader.GetFieldValueAsync<string>(6);
            node.Version = System.Version.Parse(rawVersion);
        }

        // MySQL stores capabilities as comma-separated text
        if (!await reader.IsDBNullAsync(7))
        {
            var capabilitiesStr = await reader.GetFieldValueAsync<string>(7);
            if (capabilitiesStr.IsNotEmpty())
            {
                var capabilities = capabilitiesStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                node.Capabilities.AddRange(capabilities.Select(x => new Uri(x.Trim())));
            }
        }

        return node;
    }
}
