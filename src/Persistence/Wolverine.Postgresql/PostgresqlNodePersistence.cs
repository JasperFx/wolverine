using System.Data;
using JasperFx.Events.Daemon;
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
    private readonly DbObjectName _restrictionTable;

    public PostgresqlNodePersistence(DatabaseSettings settings, PostgresqlMessageStore database,
        NpgsqlDataSource dataSource)
    {
        _settings = settings;
        _database = database;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var schemaName = settings.SchemaName ?? "public";
        _nodeTable = new DbObjectName(schemaName, NodeTableName);
        _restrictionTable = new DbObjectName(schemaName, DatabaseConstants.AgentRestrictionsTableName);
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
        await using var cmd = _dataSource.CreateCommand(
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

        var quotedSchema = _settings.SchemaName.QuoteIdentifier();
        return _dataSource.CreateCommand(
                $"delete from {_nodeTable} where id = :id;update {quotedSchema}.{IncomingTable} set {OwnerId} = 0 where {OwnerId} = :number;update {quotedSchema}.{OutgoingTable} set {OwnerId} = 0 where {OwnerId} = :number;")
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

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions,
        CancellationToken cancellationToken)
    {
        // No changes to persist. Compiling/executing an empty BatchBuilder yields a single command with
        // an empty CommandText, which throws "CommandText property has not been initialized" — so no-op
        // instead. The empty case happens on an idempotent restriction apply (no delta). See wolverine#3252.
        if (restrictions.Count == 0) return;

        var builder = new BatchBuilder();
        foreach (var restriction in restrictions)
        {
            builder.StartNewCommand();
            
            if (restriction.Type == AgentRestrictionType.None)
            {
                builder.Append($"delete from {_restrictionTable} where id = ");
                builder.AppendParameter(restriction.Id);
            }
            else
            {
                builder.Append(
                    $"insert into {_restrictionTable} (id, uri, type, node) values (");
                builder.AppendParameters(restriction.Id, restriction.AgentUri.ToString(), restriction.Type.ToString(), restriction.NodeNumber);
                builder.Append(") on conflict(id) do update set node = ");
                builder.AppendParameter(restriction.NodeNumber);
            }
        }
        
        await using var batch = builder.Compile();
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        batch.Connection = conn;
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<WolverineNode>();
        var restrictions = new List<AgentRestriction>();
        
        await using var cmd = _dataSource.CreateCommand(
            $"select {NodeColumns} from {_nodeTable};select {Id}, {NodeId}, {Started} from {_assignmentTable};select id, uri, type, node from {_restrictionTable}");
        
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

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = await reader.GetFieldValueAsync<Guid>(0, cancellationToken);
            var uriString = await reader.GetFieldValueAsync<string>(1, cancellationToken);
            var typeString = await reader.GetFieldValueAsync<string>(2, cancellationToken);
            var nodeNumber = await reader.GetFieldValueAsync<int>(3, cancellationToken);

            // TODO -- harden this against garbage data
            var restriction = new AgentRestriction(id, new Uri(uriString),
                Enum.Parse<AgentRestrictionType>(typeString), nodeNumber);
                
            restrictions.Add(restriction);
        }
        
        await reader.CloseAsync();
        
        return new(nodes, new AgentRestrictions(restrictions.ToArray()));
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

        await using var command = builder.Compile();
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

    public async Task<bool> MarkHealthCheckAsync(WolverineNode node, CancellationToken token)
    {
        var count = await _dataSource.CreateCommand($"update {_nodeTable} set health_check = now() where id = :id")
            .With("id", node.NodeId).ExecuteNonQueryAsync(token);

        // GH-3604 / D2: a miss means a peer deleted this still-live node's row; report it to the caller
        // instead of blindly re-inserting a skeleton (fresh node_number, empty capabilities) here.
        return count != 0;
    }

    public async Task ReregisterNodeAsync(WolverineNode node, CancellationToken token)
    {
        // Preserve the existing node_number (SERIAL default is overridden by the explicit value) and
        // capabilities so the resurrected row matches the identity the process still uses in memory.
        var strings = node.Capabilities.Select(x => x.ToString()).ToArray();

        await _dataSource.CreateCommand(
                $"insert into {_nodeTable} (id, node_number, uri, capabilities, description, version, health_check) values (:id, :number, :uri, :capabilities, :description, :version, now()) on conflict (id) do update set node_number = :number, uri = :uri, capabilities = :capabilities, description = :description, version = :version, health_check = now()")
            .With("id", node.NodeId)
            .With("number", node.AssignedNodeNumber)
            .With("uri", (node.ControlUri ?? TransportConstants.LocalUri).ToString())
            .With("capabilities", strings)
            .With("description", node.Description)
            .With("version", node.Version.ToString())
            .ExecuteNonQueryAsync(token);
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

        var quotedSchema = _settings.SchemaName.QuoteIdentifier();
        return await _dataSource
            .CreateCommand(
                $"select node_number, event_name, timestamp, description from {quotedSchema}.{NodeRecordTableName} order by id desc LIMIT :limit")
            .With("limit", count)
            .FetchListAsync(readRecord);
    }

    public Task DeleteOldNodeRecordsAsync(int retainCount)
    {
        if (retainCount <= 0) return Task.CompletedTask;

        var quotedSchema = _settings.SchemaName.QuoteIdentifier();
        return _dataSource.CreateCommand(
                $"delete from {quotedSchema}.{NodeRecordTableName} where id not in (select id from {quotedSchema}.{NodeRecordTableName} order by id desc limit :retain)")
            .With("retain", retainCount)
            .ExecuteNonQueryAsync();
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
    // GH-4261. Everything below shares ONE long-lived NpgsqlConnection, and Npgsql does not support
    // concurrent use of a connection. Nothing used to stop that: HasLock ran SYNCHRONOUS I/O on _conn
    // while ReleaseLockAsync / TryAttainLockAsync / DisposeAsync ran ASYNC I/O on the same field, and
    // both are reachable at once. writeHeartbeats and executeHealthChecks (WolverineRuntime.Agents.cs)
    // run on the runtime-wide Cancellation token, which shutdownAsync only cancels AFTER
    // teardownAgentsAsync has returned; teardownAgentsAsync "stops" them with Task.SafeDispose(), which
    // disposes the Task object and does nothing to the running loop. So a health check already past its
    // own cancellation guard runs ejectStaleNodes -> HasLeadershipLock() -> the ping below at the same
    // moment teardown runs NodeAgentController.StopAsync -> ReleaseLeadershipLockAsync.
    //
    // When they interleave the protocol desyncs: the second caller consumes the messages the first
    // caller's reader was waiting for, and the orphaned reader's Close never completes. Two independent
    // process dumps caught it parked forever inside NpgsqlConnection.CloseAsync -> CloseOngoingOperations
    // -> NpgsqlDataReader.Consume -> NextResult, frame-for-frame identical down to every async state
    // number, with the backend already Idle and the connector unbound from the command. CloseAsync takes
    // no CancellationToken, so HostOptions.ShutdownTimeout -- correctly threaded down to
    // WolverineRuntime.StopAsync -- could not break it, and the whole process hung: every test passed
    // and reported, then nothing exited, because the hang lives in fixture disposal after the last test.
    //
    // _gate serialises every touch of _conn. _locksLock guards the held-id list on its own, so the one
    // path that deliberately does NOT wait for the gate -- HasLock on timeout -- can still read it
    // safely. Ordering is always _gate then _locksLock, never the reverse: no _locksLock section does
    // I/O or takes the gate.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _locksLock = new();

    // Short on purpose. HasLock is synchronous and sits on the health-check tick, so it must not block
    // behind a slow release; a quarter second covers an ordinary round-trip and nothing more.
    private static readonly TimeSpan GateTimeout = 250.Milliseconds();

    // Generous on purpose. This bounds shutdown paths, where the alternative is the unbounded hang above.
    private static readonly TimeSpan CloseBudget = 5.Seconds();

    private readonly string _databaseName;
    private readonly List<int> _locks = new();
    private readonly ILogger _logger;
    private readonly NpgsqlDataSource _source;
    private NpgsqlConnection? _conn;

    public AdvisoryLock(NpgsqlDataSource source, ILogger logger, string databaseName)
    {
        _source = source;
        _logger = logger;
        _databaseName = databaseName;
    }

    public bool HasLock(int lockId)
    {
        // Cheap negative outside the gate. An id we never took cannot be held no matter what any
        // concurrent operation is doing.
        if (!holdsId(lockId)) return false;

        if (!_gate.Wait(GateTimeout))
        {
            // GH-4261: the gate is busy only because another advisory-lock operation on THIS node is in
            // flight -- a release during shutdown, or the re-attain from this same health-check tick.
            // That is not evidence the server dropped our session, and a wrong `false` here is not
            // cheap: NodeAgentController.DoHealthChecksInternalAsync reads it as lost leadership and
            // calls stepDownAsync, which is precisely the churn GH-2602 and GH-3604 were fighting. So
            // report the last state we actually established and skip the keepalive for this tick. The
            // next tick pings.
            _logger.LogDebug(
                "Advisory lock connection for database {Database} was busy; reporting the last known state of lock {LockId} without pinging",
                _databaseName, lockId);
            return true;
        }

        try
        {
            return hasLockUnsafe(lockId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The GH-2602 liveness ping. Callers MUST hold <c>_gate</c> -- it does synchronous I/O on the
    /// shared connection and may replace it.
    /// </summary>
    private bool hasLockUnsafe(int lockId)
    {
        if (_conn is null) return false;
        if (!holdsId(lockId)) return false;

        // Postgres releases session-level advisory locks the moment the
        // backend session ends — network blip, idle-connection cull,
        // pg_terminate_backend, Postgres failover, Azure flexserver
        // maintenance. Npgsql's NpgsqlConnection.State stays Open until
        // we actually try to use it, so without this ping HasLock keeps
        // claiming the lock long after another session has acquired it,
        // and two nodes both believe they're the leader. See GH-2602.
        //
        // GH-3664: this ping is only safe because the connection never has
        // an open transaction — the session reads state='idle' with
        // xact_start NULL in pg_stat_activity, so Marten's event-gap
        // liveness gate (marten#4953/#5057) never counts it as a possible
        // sequence reserver. Do NOT copy this keepalive pattern into any
        // code path that holds an open transaction: bumping state_change
        // inside a transaction makes the session look active and
        // legitimately re-promotes it to candidate reserver, freezing
        // async-daemon progress behind dead gaps.
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "select 1";
            cmd.CommandTimeout = 2;
            cmd.ExecuteScalar();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Lost advisory-lock connection for database {Database}; clearing held lock ids {Locks}",
                _databaseName, heldIds());

            clearIds();
            try
            {
                _conn.Dispose();
            }
            catch
            {
                // Already broken; nothing to do.
            }
            _conn = null;
            return false;
        }
    }

    private bool holdsId(int lockId)
    {
        lock (_locksLock) return _locks.Contains(lockId);
    }

    private bool anyIdsHeld()
    {
        lock (_locksLock) return _locks.Count > 0;
    }

    private int[] heldIds()
    {
        lock (_locksLock) return _locks.ToArray();
    }

    private void addId(int lockId)
    {
        lock (_locksLock) _locks.Add(lockId);
    }

    private void removeId(int lockId)
    {
        lock (_locksLock) _locks.Remove(lockId);
    }

    private void clearIds()
    {
        lock (_locksLock) _locks.Clear();
    }

    /// <summary>
    /// GH-3664: stamp the dedicated lock connection's <c>application_name</c> so an operator scanning
    /// pg_stat_activity can tell at a glance what is holding the session — these connections live for the
    /// process lifetime and otherwise look like anonymous idle sessions. Deliberately session-scoped SQL
    /// (set_config) rather than a connection-string edit: the NpgsqlDataSource may carry auth plumbing
    /// (e.g. Azure token callbacks) that a rebuilt connection string would lose. Best-effort — a tagging
    /// failure must never cost us the lock connection.
    /// </summary>
    private async Task tagSessionAsync(NpgsqlConnection conn, CancellationToken token)
    {
        try
        {
            // application_name is capped at NAMEDATALEN-1 (63) chars; Postgres would truncate with a
            // warning, so truncate quietly here instead.
            var name = $"wolverine-advisory-lock:{_databaseName}";
            if (name.Length > 63)
            {
                name = name[..63];
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select set_config('application_name', @name, false)";
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Unable to tag the advisory-lock session for database {Database}",
                _databaseName);
        }
    }

    public async Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            // Idempotent against repeated calls on the same session. Postgres
            // session-level advisory locks STACK ("Multiple lock requests stack,
            // so that if the same resource is locked three times it must then be
            // unlocked three times to be released" — Postgres docs). Since the
            // a84d6a262 heartbeat-renewal change calls TryAttainLeadershipLockAsync
            // every tick — including ticks where the leader already holds the
            // lock — without this short-circuit the leader's lock count grows by
            // one per heartbeat. The single ReleaseLeadershipLockAsync call
            // during DisableAgentsAsync or stepDownAsync then only decrements
            // once, leaving the lock still held server-side and silently
            // blocking failover (no error logged, just a stalled election).
            //
            // GH-4261: hasLockUnsafe, not HasLock — we already hold the gate, and SemaphoreSlim is not
            // reentrant.
            if (hasLockUnsafe(lockId))
            {
                return true;
            }

            if (_conn == null)
            {
                _conn = _source.CreateConnection();
                await _conn.OpenAsync(token).ConfigureAwait(false);
                await tagSessionAsync(_conn, token).ConfigureAwait(false);
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
                addId(lockId);
                return true;
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseLockAsync(int lockId)
    {
        if (!holdsId(lockId))
        {
            return;
        }

        // GH-4261: bounded rather than indefinite. If the gate is still held past the budget, something
        // is wedged on the connection and waiting longer only turns a released lock into a hung
        // shutdown. Postgres drops every session-level advisory lock when the backend session ends, so
        // the abandoned lock clears itself the moment this process exits.
        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to release advisory lock {LockId} for database {Identifier}; leaving it to be released when the session ends",
                lockId, _databaseName);
            return;
        }

        try
        {
            if (_conn == null || _conn.State != ConnectionState.Open)
            {
                removeId(lockId);
                return;
            }

            try
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(1.Seconds());

                await _conn.ReleaseGlobalLock(lockId, cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error trying to release advisory lock {LockId} for database {Identifier}",
                    lockId, _databaseName);
            }

            removeId(lockId);

            if (!anyIdsHeld())
            {
                await safeCloseConnectionAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn == null)
        {
            return;
        }

        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to dispose the advisory lock connection for database {Identifier}; abandoning it",
                _databaseName);
            return;
        }

        try
        {
            if (_conn == null)
            {
                return;
            }

            try
            {
                if (_conn.State == ConnectionState.Open)
                {
                    foreach (var i in heldIds())
                    {
                        try
                        {
                            await _conn.ReleaseGlobalLock(i, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogDebug(e,
                                "Error trying to release advisory lock {LockId} during dispose for database {Identifier}",
                                i, _databaseName);
                        }
                    }
                }

                await safeCloseConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error trying to dispose of advisory locks for database {Identifier}",
                    _databaseName);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Callers MUST hold <c>_gate</c>.
    /// </summary>
    private async Task safeCloseConnectionAsync()
    {
        // GH-4261: take the connection off the field FIRST. If the close below has to be abandoned, the
        // dead connection must not stay reachable for the next caller to find and use.
        var conn = _conn;
        _conn = null;
        if (conn == null) return;

        try
        {
            if (conn.State == ConnectionState.Open && !await closeWithinBudgetAsync(conn).ConfigureAwait(false))
            {
                // Deliberately no DisposeAsync: that would race the CloseAsync still parked on this
                // connection. Dropping the reference is enough -- the abandoned await sits on a
                // thread-pool thread, which cannot hold the process open, and the backend session ends
                // with the process.
                return;
            }

            await conn.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error trying to close advisory lock connection for database {Identifier}",
                _databaseName);
        }
    }

    /// <summary>
    /// GH-4261. <see cref="NpgsqlConnection.CloseAsync"/> takes no <see cref="CancellationToken"/>, so a
    /// caller's shutdown budget cannot reach it and a connection whose protocol has desynced parks in
    /// there forever draining an orphaned reader. The gate above should stop that happening at all; this
    /// makes it survivable if anything ever produces one anyway. Returns false when the close was
    /// abandoned.
    /// </summary>
    private async Task<bool> closeWithinBudgetAsync(NpgsqlConnection conn)
    {
        var closing = conn.CloseAsync();

        using var delay = new CancellationTokenSource();
        var finished = await Task.WhenAny(closing, Task.Delay(CloseBudget, delay.Token)).ConfigureAwait(false);
        await delay.CancelAsync().ConfigureAwait(false);

        if (!ReferenceEquals(finished, closing))
        {
            _logger.LogWarning(
                "Timed out after {Budget} closing the advisory lock connection for database {Identifier}; abandoning it",
                CloseBudget, _databaseName);

            // Observe the abandoned close, so that if it eventually faults the exception cannot resurface
            // as an UnobservedTaskException on the finalizer thread long after anyone could act on it.
            _ = closing.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return false;
        }

        // Surface a genuine close failure to the caller's catch rather than swallowing it here.
        await closing.ConfigureAwait(false);
        return true;
    }
}
