using ImTools;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport;

public class SqlServerQueue : Endpoint, IBrokerQueue, IDatabaseBackedEndpoint
{
    internal static Uri ToUri(string name, string? databaseName)
    {
        return databaseName.IsEmpty()
            ? new Uri($"{SqlServerTransport.ProtocolName}://{name}")
            : new Uri($"{SqlServerTransport.ProtocolName}://{name}/{databaseName}");
    }

    private readonly string _queueTableName;
    private readonly string _scheduledTableName;
    private bool _hasInitialized;
    private ISqlServerQueueSender? _sender;
    private ImHashMap<string, bool> _checkedDatabases = ImHashMap<string, bool>.Empty;
    private readonly Lazy<QueueTable> _queueTable;
    private readonly Lazy<ScheduledMessageTable> _scheduledTable;

    public SqlServerQueue(string name, SqlServerTransport parent, EndpointRole role = EndpointRole.Application,
        string? databaseName = null) : base(ToUri(name, databaseName), role)
    {
        Parent = parent;
        _queueTableName = $"wolverine_queue_{name}";
        _scheduledTableName = $"wolverine_queue_{name}_scheduled";

        Mode = EndpointMode.Durable;
        Name = name;
        EndpointName = name;

        // Gotta be lazy so the schema names get set
        _queueTable = new Lazy<QueueTable>(() => new QueueTable(Parent, _queueTableName));
        _scheduledTable = new Lazy<ScheduledMessageTable>(() => new ScheduledMessageTable(Parent, _scheduledTableName));
    }

    public string Name { get; }

    internal SqlServerTransport Parent { get; }

    internal Table QueueTable => _queueTable.Value;

    internal Table ScheduledTable => _scheduledTable.Value;

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.Durable || mode == EndpointMode.BufferedInMemory;
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(runtime.LoggerFactory.CreateLogger<SqlServerQueue>());
        }

        if (Parent.Databases != null)
        {
            var mtListener = new MultiTenantedQueueListener(
                runtime.LoggerFactory.CreateLogger<MultiTenantedQueueListener>(), this, Parent.Databases, runtime,
                receiver);

            await mtListener.StartAsync();
            return mtListener;
        }

        var listener = new SqlServerQueueListener(this, runtime, receiver);
        await listener.StartAsync();
        return listener;
    }

    private void buildSenderIfMissing()
    {
        if (_sender != null) return;

        if (Parent.Databases != null)
        {
            _sender = new MultiTenantedQueueSender(this, Parent.Databases);
        }
        else
        {
            _sender = new SqlServerQueueSender(this);
        }
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        buildSenderIfMissing();
        return _sender!;
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        if (Parent.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }

        _hasInitialized = true;
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        buildSenderIfMissing();
        return _sender!.SendAsync(envelope);
    }

    private async ValueTask forEveryDatabase(Func<string, string, Task> action)
    {
        if (Parent.Databases != null)
        {
            // Multi-tenant mode - iterate over all tenant databases
            foreach (var database in Parent.Databases.ActiveDatabases().OfType<SqlServerMessageStore>())
            {
                await action(database.Settings.ConnectionString, database.Identifier);
            }
        }
        else
        {
            // Single-tenant mode - use the transport's connection string
            await action(Parent.Settings.ConnectionString, Parent.Settings.SchemaName ?? "wolverine");
        }
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return forEveryDatabase(async (connectionString, _) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            try
            {
                await conn.CreateCommand($"delete from {QueueTable.Identifier}").ExecuteNonQueryAsync();
                await conn.CreateCommand($"delete from {ScheduledTable.Identifier}").ExecuteNonQueryAsync();
            }
            finally
            {
                await conn.CloseAsync();
            }
        });
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var count = await CountAsync();
        var scheduled = await ScheduledCountAsync();

        return new Dictionary<string, string>
            { { "Name", Name }, { "Count", count.ToString() }, { "Scheduled", scheduled.ToString() } };
    }

    public async ValueTask<bool> CheckAsync()
    {
        var returnValue = true;
        await forEveryDatabase(async (connectionString, _) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            try
            {
                var queueDelta = await QueueTable.FindDeltaAsync(conn);
                if (queueDelta.HasChanges())
                {
                    returnValue = false;
                    return;
                }

                var scheduledDelta = await ScheduledTable.FindDeltaAsync(conn);

                returnValue = returnValue && !scheduledDelta.HasChanges();
            }
            finally
            {
                await conn.CloseAsync();
            }
        });

        return returnValue;
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        await forEveryDatabase(async (connectionString, _) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await QueueTable.Drop(conn);
            await ScheduledTable.Drop(conn);

            await conn.CloseAsync();
        });
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await forEveryDatabase(async (connectionString, identifier) =>
        {
            await EnsureSchemaExists(identifier, connectionString);
        });
    }

    internal async Task EnsureSchemaExists(string identifier, string connectionString)
    {
        if (_checkedDatabases.Contains(identifier)) return;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await QueueTable.ApplyChangesAsync(conn);
        await ScheduledTable.ApplyChangesAsync(conn);

        await conn.CloseAsync();

        _checkedDatabases = _checkedDatabases.AddOrUpdate(identifier, true);
    }

    public async Task<long> CountAsync()
    {
        var count = 0L;
        await forEveryDatabase(async (connectionString, _) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            try
            {
                count += (int)await conn.CreateCommand($"select count(*) from {QueueTable.Identifier}")
                    .ExecuteScalarAsync();
            }
            finally
            {
                await conn.CloseAsync();
            }
        });

        return count;
    }

    public async Task<long> ScheduledCountAsync()
    {
        var count = 0L;
        await forEveryDatabase(async (connectionString, _) =>
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            try
            {
                count += (int)await conn.CreateCommand($"select count(*) from {ScheduledTable.Identifier}")
                    .ExecuteScalarAsync();
            }
            finally
            {
                await conn.CloseAsync();
            }
        });

        return count;
    }

    public Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellation)
    {
        buildSenderIfMissing();
        return _sender!.ScheduleRetryAsync(envelope, cancellation);
    }

    #region Test helper methods - These provide backward compatibility for tests

    private string? _writeDirectlyToQueueTableSql;
    private string? _writeDirectlyToTheScheduledTable;
    private string? _moveFromOutgoingToQueueSql;
    private string? _moveFromOutgoingToScheduledSql;
    private string? _moveScheduledToReadyQueueSql;
    private string? _deleteExpiredSql;
    private string? _tryPopMessagesDirectlySql;
    private string? _tryPopMessagesToInboxSql;

    private void buildTestSqlIfMissing()
    {
        if (_writeDirectlyToQueueTableSql != null) return;

        _writeDirectlyToQueueTableSql =
            $@"insert into {QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
merge {ScheduledTable.Identifier} as target
using (values (@id, @body, @type, @expires, @time)) as source ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
on target.id = @id
WHEN MATCHED THEN UPDATE set target.body = @body, target.{DatabaseConstants.ExecutionTime} = @time
WHEN NOT MATCHED THEN INSERT  ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime}) values (source.{DatabaseConstants.Id}, source.{DatabaseConstants.Body}, source.{DatabaseConstants.MessageType}, source.{DatabaseConstants.KeepUntil}, source.{DatabaseConstants.ExecutionTime});
";

        _moveFromOutgoingToQueueSql = $@"
INSERT into {QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM
    {Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time, {DatabaseConstants.DeliverBy}
FROM
    {Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveScheduledToReadyQueueSql = $@"
select id, body, message_type, keep_until into #temp_move_{Name}
FROM {ScheduledTable.Identifier} WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE {DatabaseConstants.ExecutionTime} <= SYSDATETIMEOFFSET() AND ID NOT IN (select id from {QueueTable.Identifier})
ORDER BY {ScheduledTable.Identifier}.timestamp;
delete from {ScheduledTable.Identifier} where id in (select id from #temp_move_{Name});
INSERT INTO {QueueTable.Identifier}
(id, body, message_type, keep_until)
 SELECT id, body, message_type, keep_until FROM #temp_move_{Name};
select count(*) from #temp_move_{Name}
";

        _deleteExpiredSql =
            $"delete from {QueueTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= SYSDATETIMEOFFSET();delete from {ScheduledTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= SYSDATETIMEOFFSET()";

        _tryPopMessagesDirectlySql = $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

WITH message AS (
    SELECT TOP(@count) {DatabaseConstants.Body}, {DatabaseConstants.KeepUntil}
    FROM {QueueTable.Identifier} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY {QueueTable.Identifier}.timestamp)
DELETE FROM message
OUTPUT
    deleted.{DatabaseConstants.Body};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";

        _tryPopMessagesToInboxSql = $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

delete FROM {QueueTable.Identifier} WITH (UPDLOCK, READPAST, ROWLOCK) where id in (select id from {Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable});
select top(@count) id, body, message_type, keep_until into #temp_pop_{Name}
FROM {QueueTable.Identifier} WITH (UPDLOCK, READPAST, ROWLOCK)
ORDER BY {QueueTable.Identifier}.timestamp;
delete from {QueueTable.Identifier} where id in (select id from #temp_pop_{Name});
INSERT INTO {Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable}
(id, status, owner_id, body, message_type, received_at, keep_until)
 SELECT id, 'Incoming', @node, body, message_type, '{Uri}', keep_until FROM #temp_pop_{Name};
select body from #temp_pop_{Name};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);

        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            await scheduleMessageAsync(envelope, cancellationToken, conn);
        }
        else
        {
            try
            {
                await conn.OpenAsync(cancellationToken);
                await conn.CreateCommand(_writeDirectlyToQueueTableSql!)
                    .With("id", envelope.Id)
                    .With("body", EnvelopeSerializer.Serialize(envelope))
                    .With("type", envelope.MessageType)
                    .With("expires", envelope.DeliverBy)
                    .ExecuteNonQueryAsync(cancellationToken);
                await conn.CloseAsync();
            }
            catch (SqlException e)
            {
                // Making this idempotent, but optimistically
                if (e.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint")) return;
                throw;
            }
        }
    }

    private async Task scheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken, SqlConnection conn)
    {
        buildTestSqlIfMissing();
        await conn.OpenAsync(cancellationToken);
        await conn.CreateCommand(_writeDirectlyToTheScheduledTable!)
            .With("id", envelope.Id)
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("expires", envelope.DeliverBy)
            .With("time", envelope.ScheduledTime)
            .ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
    }

    public async Task ScheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await scheduleMessageAsync(envelope, cancellationToken, conn);
    }

    public async Task MoveFromOutgoingToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);

        await conn.OpenAsync(cancellationToken);

        try
        {
            var count = await conn.CreateCommand(_moveFromOutgoingToQueueSql!)
                .With("id", envelope.Id)
                .ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException("No matching outgoing envelope");
        }
        catch (SqlException e)
        {
            // Making this idempotent, but optimistically
            if (e.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint")) return;
            throw;
        }

        await conn.CloseAsync();
    }

    public async Task MoveFromOutgoingToScheduledAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        if (!envelope.ScheduledTime.HasValue)
            throw new InvalidOperationException("This envelope has no scheduled time");

        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);

        await conn.OpenAsync(cancellationToken);
        try
        {
            var count = await conn.CreateCommand(_moveFromOutgoingToScheduledSql!)
                .With("id", envelope.Id)
                .With("time", envelope.ScheduledTime!.Value)
                .ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException($"No matching outgoing envelope for {envelope}");
        }
        catch (SqlException e)
        {
            if (e.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint"))
            {
                await conn.CreateCommand(
                        $"delete from {Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
                    .With("id", envelope.Id)
                    .ExecuteNonQueryAsync(cancellationToken);

                return;
            }

            throw;
        }

        await conn.CloseAsync();
    }

    public async Task<int> MoveScheduledToReadyQueueAsync(CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);

        await conn.OpenAsync(cancellationToken);
        var count = (int)await conn.CreateCommand(_moveScheduledToReadyQueueSql!)
            .ExecuteScalarAsync(cancellationToken);

        await conn.CloseAsync();

        return count;
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await conn.CreateCommand(_deleteExpiredSql!).ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
    }

    public async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, ILogger logger,
        CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        return await conn
            .CreateCommand(_tryPopMessagesDirectlySql!)
            .With("count", count)
            .FetchListAsync<Envelope>(async reader =>
            {
                var data = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
                try
                {
                    return EnvelopeSerializer.Deserialize(data);
                }
                catch (Exception e)
                {
                    logger.LogError(e,
                        "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding", Name);
                    return Envelope.ForPing(Uri); // just a stand in
                }
            }, cancellation: cancellationToken);
    }

    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(int count, DurabilitySettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        buildTestSqlIfMissing();
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        return await conn
            .CreateCommand(_tryPopMessagesToInboxSql!)
            .With("count", count)
            .With("node", settings.AssignedNodeNumber)
            .FetchListAsync<Envelope>(async reader =>
            {
                var data = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
                try
                {
                    return EnvelopeSerializer.Deserialize(data);
                }
                catch (Exception e)
                {
                    logger.LogError(e,
                        "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding", Name);
                    return Envelope.ForPing(Uri); // just a stand in
                }
            }, cancellation: cancellationToken);
    }

    #endregion
}
