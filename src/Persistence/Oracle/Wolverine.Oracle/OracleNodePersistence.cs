using System.Data;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Oracle;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Oracle;

internal class OracleNodePersistence : DatabaseConstants, INodeAgentPersistence
{
    public static int LeaderLockId = 9999999;
    private readonly DbObjectName _assignmentTable;
    private readonly IMessageDatabase _database;
    private readonly OracleDataSource _dataSource;
    private readonly int _lockId;
    private readonly DbObjectName _nodeTable;
    private readonly DatabaseSettings _settings;
    private readonly DbObjectName _restrictionTable;

    public OracleNodePersistence(DatabaseSettings settings, OracleMessageStore database, OracleDataSource dataSource)
    {
        _settings = settings;
        _database = database;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schemaName = (settings.SchemaName ?? "WOLVERINE").ToUpperInvariant();
        _nodeTable = new DbObjectName(schemaName, NodeTableName.ToUpperInvariant());
        _restrictionTable = new DbObjectName(schemaName, AgentRestrictionsTableName.ToUpperInvariant());
        _assignmentTable = new DbObjectName(schemaName, NodeAssignmentsTableName.ToUpperInvariant());

        _lockId = schemaName.GetDeterministicHashCode();
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var cmd = conn.CreateCommand($"DELETE FROM {_nodeTable}");
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var capabilities = string.Join(",", node.Capabilities.Select(x => x.ToString()));

        var insertCmd = conn.CreateCommand(
            $"INSERT INTO {_nodeTable} (id, uri, capabilities, description, version) VALUES (:id, :uri, :capabilities, :description, :version)");
        insertCmd.With("id", node.NodeId);
        insertCmd.With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString());
        insertCmd.With("capabilities", capabilities);
        insertCmd.With("description", node.Description);
        insertCmd.With("version", node.Version.ToString());

        await insertCmd.ExecuteNonQueryAsync(cancellationToken);

        // Get the auto-generated node_number
        var selectCmd = conn.CreateCommand($"SELECT node_number FROM {_nodeTable} WHERE id = :id");
        selectCmd.With("id", node.NodeId);
        var result = await selectCmd.ExecuteScalarAsync(cancellationToken);

        await conn.CloseAsync();

        return Convert.ToInt32(result);
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        if (_database.HasDisposed) return;

        await using var conn = await _dataSource.OpenConnectionAsync();

        var cmd1 = conn.CreateCommand($"DELETE FROM {_nodeTable} WHERE id = :id");
        cmd1.With("id", nodeId);
        await cmd1.ExecuteNonQueryAsync();

        var cmd2 = conn.CreateCommand(
            $"UPDATE {_settings.SchemaName}.{IncomingTable} SET {OwnerId} = 0 WHERE {OwnerId} = :nodeNum");
        cmd2.With("nodeNum", assignedNodeNumber);
        await cmd2.ExecuteNonQueryAsync();

        var cmd3 = conn.CreateCommand(
            $"UPDATE {_settings.SchemaName}.{OutgoingTable} SET {OwnerId} = 0 WHERE {OwnerId} = :nodeNum");
        cmd3.With("nodeNum", assignedNodeNumber);
        await cmd3.ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var nodeCmd = conn.CreateCommand($"SELECT {NodeColumns} FROM {_nodeTable}");
        await using var nodeReader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
        while (await nodeReader.ReadAsync(cancellationToken))
        {
            var node = await readNodeAsync(nodeReader);
            nodes.Add(node);
        }
        await nodeReader.CloseAsync();

        var dict = nodes.ToDictionary(x => x.NodeId);

        var assignCmd = conn.CreateCommand($"SELECT {Id}, {NodeId}, {Started} FROM {_assignmentTable}");
        await using var assignReader = await assignCmd.ExecuteReaderAsync(cancellationToken);
        while (await assignReader.ReadAsync(cancellationToken))
        {
            var agentId = new Uri(await assignReader.GetFieldValueAsync<string>(0, cancellationToken));
            var nid = await OracleEnvelopeReader.ReadGuidAsync(assignReader, 1, cancellationToken);

            if (dict.TryGetValue(nid, out var node))
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
            if (restriction.Type == AgentRestrictionType.None)
            {
                var cmd = conn.CreateCommand($"DELETE FROM {_restrictionTable} WHERE id = :id");
                cmd.With("id", restriction.Id);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                var cmd = conn.CreateCommand(
                    $"MERGE INTO {_restrictionTable} t USING DUAL ON (t.id = :id) " +
                    "WHEN MATCHED THEN UPDATE SET t.node = :node " +
                    "WHEN NOT MATCHED THEN INSERT (id, uri, type, node) VALUES (:id, :uri, :type, :node)");
                cmd.With("id", restriction.Id);
                cmd.With("uri", restriction.AgentUri.ToString());
                cmd.With("type", restriction.Type.ToString());
                cmd.With("node", restriction.NodeNumber);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await conn.CloseAsync();
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();
        var restrictions = new List<AgentRestriction>();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var nodeCmd = conn.CreateCommand($"SELECT {NodeColumns} FROM {_nodeTable}");
        await using var nodeReader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
        while (await nodeReader.ReadAsync(cancellationToken))
        {
            var node = await readNodeAsync(nodeReader);
            nodes.Add(node);
        }
        await nodeReader.CloseAsync();

        var dict = nodes.ToDictionary(x => x.NodeId);

        var assignCmd = conn.CreateCommand($"SELECT {Id}, {NodeId}, {Started} FROM {_assignmentTable}");
        await using var assignReader = await assignCmd.ExecuteReaderAsync(cancellationToken);
        while (await assignReader.ReadAsync(cancellationToken))
        {
            var agentId = new Uri(await assignReader.GetFieldValueAsync<string>(0, cancellationToken));
            var nid = await OracleEnvelopeReader.ReadGuidAsync(assignReader, 1, cancellationToken);

            if (dict.TryGetValue(nid, out var node))
            {
                node.ActiveAgents.Add(agentId);
            }
        }
        await assignReader.CloseAsync();

        var restrictCmd = conn.CreateCommand($"SELECT id, uri, type, node FROM {_restrictionTable}");
        await using var restrictReader = await restrictCmd.ExecuteReaderAsync(cancellationToken);
        while (await restrictReader.ReadAsync(cancellationToken))
        {
            var id = await OracleEnvelopeReader.ReadGuidAsync(restrictReader, 0, cancellationToken);
            var uriString = await restrictReader.GetFieldValueAsync<string>(1, cancellationToken);
            var typeString = await restrictReader.GetFieldValueAsync<string>(2, cancellationToken);
            var nodeNumber = Convert.ToInt32(restrictReader.GetValue(3));

            restrictions.Add(new AgentRestriction(id, new Uri(uriString),
                Enum.Parse<AgentRestrictionType>(typeString), nodeNumber));
        }
        await restrictReader.CloseAsync();
        await conn.CloseAsync();

        return new NodeAgentState(nodes, new AgentRestrictions(restrictions.ToArray()));
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        if (_database.HasDisposed) return null;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        WolverineNode? returnValue = null;

        var nodeCmd = conn.CreateCommand($"SELECT {NodeColumns} FROM {_nodeTable} WHERE id = :id");
        nodeCmd.With("id", nodeId);
        await using var nodeReader = await nodeCmd.ExecuteReaderAsync(cancellationToken);
        if (await nodeReader.ReadAsync(cancellationToken))
        {
            returnValue = await readNodeAsync(nodeReader);
        }
        await nodeReader.CloseAsync();

        if (returnValue != null)
        {
            var assignCmd = conn.CreateCommand(
                $"SELECT {Id}, {NodeId}, {Started} FROM {_assignmentTable} WHERE node_id = :id");
            assignCmd.With("id", nodeId);
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
            var cmd = conn.CreateCommand(
                $"MERGE INTO {_assignmentTable} t USING DUAL ON (t.id = :id) " +
                "WHEN MATCHED THEN UPDATE SET t.node_id = :node " +
                "WHEN NOT MATCHED THEN INSERT (id, node_id) VALUES (:id, :node)");
            cmd.With("id", agent.ToString());
            cmd.With("node", nodeId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await conn.CloseAsync();
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var cmd = conn.CreateCommand($"DELETE FROM {_assignmentTable} WHERE id = :id AND node_id = :node");
        cmd.With("id", agentUri.ToString());
        cmd.With("node", nodeId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var cmd = conn.CreateCommand(
            $"MERGE INTO {_assignmentTable} t USING DUAL ON (t.id = :id) " +
            "WHEN MATCHED THEN UPDATE SET t.node_id = :node " +
            "WHEN NOT MATCHED THEN INSERT (id, node_id) VALUES (:id, :node)");
        cmd.With("id", agentUri.ToString());
        cmd.With("node", nodeId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        var cmd = conn.CreateCommand($"UPDATE {_nodeTable} SET health_check = :now WHERE id = :id");
        cmd.With("id", nodeId);
        cmd.With("now", lastHeartbeatTime);
        await cmd.ExecuteNonQueryAsync();
        await conn.CloseAsync();
    }

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token);
        var cmd = conn.CreateCommand(
            $"UPDATE {_nodeTable} SET health_check = SYS_EXTRACT_UTC(SYSTIMESTAMP) WHERE id = :id");
        cmd.With("id", node.NodeId);
        var count = await cmd.ExecuteNonQueryAsync(token);

        if (count == 0)
        {
            await conn.CloseAsync();
            await PersistAsync(node, token);
            return;
        }

        await conn.CloseAsync();
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

        await using var conn = await _dataSource.OpenConnectionAsync();
        var cmd = conn.CreateCommand(
            $"SELECT node_number, event_name, timestamp, description FROM {_settings.SchemaName}.{NodeRecordTableName} " +
            $"ORDER BY id DESC FETCH FIRST :limit ROWS ONLY");
        cmd.With("limit", count);

        var list = new List<NodeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new NodeRecord
            {
                NodeNumber = Convert.ToInt32(reader.GetValue(0)),
                RecordType = Enum.Parse<NodeRecordType>(await reader.GetFieldValueAsync<string>(1)),
                Timestamp = await reader.GetFieldValueAsync<DateTimeOffset>(2),
                Description = await reader.GetFieldValueAsync<string>(3)
            });
        }
        await conn.CloseAsync();

        return list;
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
            NodeId = OracleEnvelopeReader.ReadGuid(reader, 0),
            AssignedNodeNumber = Convert.ToInt32(reader.GetValue(1)),
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
