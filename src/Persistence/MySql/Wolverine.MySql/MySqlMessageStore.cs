using System.Data.Common;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.MySql;
using Wolverine.Logging;
using Wolverine.MySql.Sagas;
using Wolverine.MySql.Schema;
using Wolverine.MySql.Util;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;
using Table = Weasel.MySql.Tables.Table;

namespace Wolverine.MySql;

internal class MySqlMessageStore : MessageDatabase<MySqlConnection>
{
    private readonly string _findAtLargeEnvelopesSql;

    private ImHashMap<Type, IDatabaseSagaSchema> _sagaStorage = ImHashMap<Type, IDatabaseSagaSchema>.Empty;

    public MySqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings,
        MySqlDataSource dataSource,
        ILogger<MySqlMessageStore> logger) : this(databaseSettings, settings, dataSource, logger,
        Array.Empty<SagaTableDefinition>())
    {
        var descriptor = Describe();
        Id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
    }

    public MySqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings,
        MySqlDataSource dataSource,
        ILogger<MySqlMessageStore> logger, IEnumerable<SagaTableDefinition> sagaTypes) : base(databaseSettings,
        dataSource,
        settings, logger, new MySqlMigrator(), MySqlProvider.Instance)
    {
        _findAtLargeEnvelopesSql =
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE owner_id = {TransportConstants.AnyNode} AND status = '{EnvelopeStatus.Incoming}' AND {DatabaseConstants.ReceivedAt} = @address LIMIT @limit";

        MySqlDataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        AdvisoryLock = new MySqlAdvisoryLock(dataSource, logger, Identifier);

        foreach (var sagaTableDefinition in sagaTypes)
        {
            var storage = typeof(DatabaseSagaSchema<,>).CloseAndBuildAs<IDatabaseSagaSchema>(sagaTableDefinition,
                _settings, sagaTableDefinition.SagaType, sagaTableDefinition.IdMember.GetMemberType());
            _sagaStorage = _sagaStorage.AddOrUpdate(sagaTableDefinition.SagaType, storage);
        }
    }

    public MySqlDataSource MySqlDataSource { get; }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource)
    {
        return new MySqlNodePersistence(databaseSettings, this, (MySqlDataSource)dataSource);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        if (ex is MySqlException mySqlException)
        {
            return mySqlException.Number == 1062;
        }

        return false;
    }

    protected override void writePagingAfter(DbCommandBuilder builder, int offset, int limit)
    {
        if (limit > 0)
        {
            builder.Append(" LIMIT ");
            builder.AppendParameter(limit);
        }

        if (offset > 0)
        {
            builder.Append(" OFFSET ");
            builder.AppendParameter(offset);
        }
    }

    public override ISchemaObject AddExternalMessageTable(ExternalMessageTable definition)
    {
        var table = new Table(definition.TableName);
        table.AddColumn<Guid>(definition.IdColumnName).AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "JSON").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn<DateTimeOffset>(definition.TimestampColumnName)
                .DefaultValueByExpression("(UTC_TIMESTAMP(6))");
        }

        if (definition.MessageTypeColumnName.IsNotEmpty())
        {
            table.AddColumn<string>(definition.MessageTypeColumnName);
        }

        return table;
    }

    public override async Task MigrateExternalMessageTable(ExternalMessageTable definition)
    {
        var table = (Table)AddExternalMessageTable(definition);
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, CancellationToken.None, table);
        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new MySqlMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
        }

        await conn.CloseAsync();
    }

    protected override Task deleteMany(DbTransaction tx, Guid[] ids, DbObjectName tableName,
        string idColumnName)
    {
        if (ids.Length == 0) return Task.CompletedTask;

        var cmd = (MySqlCommand)tx.Connection!.CreateCommand();
        cmd.Transaction = (MySqlTransaction)tx;

        var placeholders = MySqlCommandExtensions.WithIdList(cmd, "id", ids);
        cmd.CommandText = $"DELETE FROM {tableName.QualifiedName} WHERE {idColumnName} IN ({placeholders})";

        return cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<bool> TryAttainLockAsync(int lockId, MySqlConnection connection,
        CancellationToken token)
    {
        var lockName = $"wolverine_{lockId}";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GET_LOCK(@lockName, 0)";
        cmd.Parameters.AddWithValue("@lockName", lockName);

        var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);

        if (result is int intResult && intResult == 1) return true;
        if (result is long longResult && longResult == 1) return true;

        return false;
    }

    protected override DbCommand buildFetchSql(MySqlConnection conn, DbObjectName tableName, string[] columnNames,
        int maxRecords)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {columnNames.Join(", ")} FROM {tableName.QualifiedName} LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", maxRecords);
        return cmd;
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using (var reader = await CreateCommand(
            $"SELECT status, COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable} GROUP BY status")
            .ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                var count = await reader.GetFieldValueAsync<long>(1);

                if (status == EnvelopeStatus.Incoming)
                {
                    counts.Incoming = (int)count;
                }
                else if (status == EnvelopeStatus.Handled)
                {
                    counts.Handled = (int)count;
                }
                else if (status == EnvelopeStatus.Scheduled)
                {
                    counts.Scheduled = (int)count;
                }
            }

            await reader.CloseAsync();
        }

        var longCount =
            await CreateCommand($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.OutgoingTable}")
                .ExecuteScalarAsync();

        counts.Outgoing = Convert.ToInt32(longCount);

        var deadLetterCount =
            await CreateCommand($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.DeadLetterTable}")
                .ExecuteScalarAsync();

        counts.DeadLetter = Convert.ToInt32(deadLetterCount);

        return counts;
    }

    public override async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        try
        {
            if (discards.Length > 0)
            {
                var deleteCmd = conn.CreateCommand();
                var deletePlaceholders = MySqlCommandExtensions.WithEnvelopeIds(deleteCmd, "id", discards);
                deleteCmd.CommandText =
                    $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id IN ({deletePlaceholders})";
                await deleteCmd.ExecuteNonQueryAsync(_cancellation);
            }

            if (reassigned.Length > 0)
            {
                var reassignCmd = conn.CreateCommand();
                var reassignPlaceholders = MySqlCommandExtensions.WithEnvelopeIds(reassignCmd, "rid", reassigned);
                reassignCmd.CommandText =
                    $"UPDATE {SchemaName}.{DatabaseConstants.OutgoingTable} SET owner_id = @node WHERE id IN ({reassignPlaceholders})";
                reassignCmd.Parameters.AddWithValue("@node", nodeId);
                await reassignCmd.ExecuteNonQueryAsync(_cancellation);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return;
        if (envelopes.Length == 0) return;

        await using var conn = await MySqlDataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand();
        var placeholders = MySqlCommandExtensions.WithEnvelopeIds(cmd, "id", envelopes);
        cmd.CommandText = $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id IN ({placeholders})";
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"SELECT {DatabaseConstants.OutgoingFields} FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE owner_id = {TransportConstants.AnyNode} AND destination = @destination LIMIT {settings.RecoveryBatchSize}";
    }

    public override async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress,
        int limit)
    {
        await using var conn = await MySqlDataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand();
        cmd.CommandText = _findAtLargeEnvelopesSql;
        cmd.Parameters.AddWithValue("@address", listenerAddress.ToString());
        cmd.Parameters.AddWithValue("@limit", limit);
        var result = await cmd.FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
        await conn.CloseAsync();
        return result;
    }

    public override DbCommandBuilder ToCommandBuilder()
    {
        return new DbCommandBuilder(new MySqlCommand());
    }

    public override async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (HasDisposed) return false;

        await using var conn = await MySqlDataSource.OpenConnectionAsync(cancellation);
        var cmd = conn.CreateCommand();

        if (Durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            cmd.CommandText =
                $"SELECT COUNT(id) FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", envelope.Id);
        }
        else
        {
            cmd.CommandText =
                $"SELECT COUNT(id) FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = @id AND {DatabaseConstants.ReceivedAt} = @destination";
            cmd.Parameters.AddWithValue("@id", envelope.Id);
            cmd.Parameters.AddWithValue("@destination", envelope.Destination!.ToString());
        }

        var count = await cmd.ExecuteScalarAsync(cancellation);
        await conn.CloseAsync();

        return Convert.ToInt64(count) > 0;
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE status = '{EnvelopeStatus.Scheduled}' AND execution_time <= ");

        builder.AppendParameter(utcNow);
        builder.Append($" ORDER BY execution_time LIMIT {Durability.RecoveryBatchSize}");
    }

    public override async Task PollForScheduledMessagesAsync(IWolverineRuntime runtime, ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        IReadOnlyList<Envelope> envelopes;

        if (HasDisposed) return;

        await using var conn = await MySqlDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var tx = await conn.BeginTransactionAsync(cancellationToken);

            var lockName = $"wolverine_{Settings.ScheduledJobLockId}";
            var lockCmd = conn.CreateCommand();
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT GET_LOCK(@lockName, 0)";
            lockCmd.Parameters.AddWithValue("@lockName", lockName);
            var lockResult = await lockCmd.ExecuteScalarAsync(cancellationToken);

            var gotLock = (lockResult is int intResult && intResult == 1) ||
                          (lockResult is long longResult && longResult == 1);

            if (gotLock)
            {
                var builder = new DbCommandBuilder(new MySqlCommand());
                WriteLoadScheduledEnvelopeSql(builder, DateTimeOffset.UtcNow);
                var cmd = (MySqlCommand)builder.Compile();
                cmd.Connection = conn;
                cmd.Transaction = tx;

                envelopes = await cmd.FetchListAsync(reader =>
                    DatabasePersistence.ReadIncomingAsync(reader, cancellationToken), cancellation: cancellationToken);

                if (!envelopes.Any())
                {
                    var releaseLockCmd = conn.CreateCommand();
                    releaseLockCmd.Transaction = tx;
                    releaseLockCmd.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                    releaseLockCmd.Parameters.AddWithValue("@lockName", lockName);
                    await releaseLockCmd.ExecuteScalarAsync(cancellationToken);

                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                var ids = envelopes.Select(x => x.Id).ToArray();
                var reassignCmd = conn.CreateCommand();
                reassignCmd.Transaction = tx;
                var placeholders = MySqlCommandExtensions.WithIdList(reassignCmd, "id", ids);
                reassignCmd.CommandText =
                    $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET owner_id = @owner, status = '{EnvelopeStatus.Incoming}' WHERE id IN ({placeholders})";
                reassignCmd.Parameters.AddWithValue("@owner", durabilitySettings.AssignedNodeNumber);
                await reassignCmd.ExecuteNonQueryAsync(_cancellation);

                var releaseLockCmd2 = conn.CreateCommand();
                releaseLockCmd2.Transaction = tx;
                releaseLockCmd2.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                releaseLockCmd2.Parameters.AddWithValue("@lockName", lockName);
                await releaseLockCmd2.ExecuteScalarAsync(cancellationToken);

                await tx.CommitAsync(cancellationToken);

                await runtime.EnqueueDirectlyAsync(envelopes);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override async Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName,
        byte[] json,
        CancellationToken token)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        var cmd = conn.CreateCommand();
        if (table.MessageTypeColumnName.IsEmpty())
        {
            cmd.CommandText =
                $"INSERT INTO {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}) VALUES (@id, @json)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@json", json);
        }
        else
        {
            cmd.CommandText =
                $"INSERT INTO {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) VALUES (@id, @json, @message)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@message", messageTypeName);
        }

        await cmd.ExecuteNonQueryAsync(token);

        await conn.CloseAsync();
    }

    public override DatabaseDescriptor Describe()
    {
        var builder = new MySqlConnectionStringBuilder(DataSource?.ConnectionString ?? Settings.ConnectionString ?? "");
        var descriptor = new DatabaseDescriptor
        {
            Engine = "MySQL",
            ServerName = builder.Server ?? string.Empty,
            DatabaseName = builder.Database ?? string.Empty,
            Subject = GetType().FullNameInCode(),
            SubjectUri = SubjectUri,
            Identifier = Identifier
        };

        descriptor.TenantIds.AddRange(TenantIds);

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Server));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Port));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Database));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.UserID));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Pooling));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MinimumPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaximumPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.DefaultCommandTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SslMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CharacterSet));

        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("password"));
        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("certificate"));

        return descriptor;
    }

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(Durability, SchemaName);
        yield return new IncomingEnvelopeTable(Durability, SchemaName);
        yield return new DeadLettersTable(Durability, SchemaName);

        if (Role == MessageStoreRole.Main)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            // MySQL requires AUTO_INCREMENT to be part of a key - add unique index
            nodeTable.AddColumn("node_number", "INT AUTO_INCREMENT").NotNull()
                .AddIndex(idx => idx.IsUnique = true);
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("(UTC_TIMESTAMP(6))").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").NotNull().DefaultValueByExpression("(UTC_TIMESTAMP(6))");
            nodeTable.AddColumn<string>("version");
            nodeTable.AddColumn("capabilities", "TEXT").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id")
                .ForeignKeyTo(nodeTable.Identifier, "id", onDelete: Weasel.Core.CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("(UTC_TIMESTAMP(6))").NotNull();

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "LONGBLOB").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("(UTC_TIMESTAMP(6))");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }

            if (_settings.AddTenantLookupTable)
            {
                var tenantTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.TenantsTableName));
                tenantTable.AddColumn<string>(StorageConstants.TenantIdColumn).AsPrimaryKey();
                tenantTable.AddColumn<string>(StorageConstants.ConnectionStringColumn).NotNull();
                yield return tenantTable;
            }

            var eventTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn("id", "INT AUTO_INCREMENT").AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn<string>("event_name").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("(UTC_TIMESTAMP(6))").NotNull();
            eventTable.AddColumn<string>("description").AllowNulls();
            yield return eventTable;

            var restrictionTable =
                new Table(new DbObjectName(SchemaName, DatabaseConstants.AgentRestrictionsTableName));
            restrictionTable.AddColumn<Guid>("id").AsPrimaryKey();
            restrictionTable.AddColumn<string>("uri").NotNull();
            restrictionTable.AddColumn<string>("type").NotNull();
            restrictionTable.AddColumn<int>("node").NotNull().DefaultValue(0);
            yield return restrictionTable;
        }

        foreach (var table in _otherTables)
        {
            yield return table;
        }

        foreach (var entry in _sagaStorage.Enumerate())
        {
            yield return entry.Value.Table;
        }
    }

    private readonly List<Table> _otherTables = new();

    public void AddTable(Table table)
    {
        _otherTables.Add(table);
    }

    public override DatabaseSagaSchema<T, TId> SagaSchemaFor<T, TId>()
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is DatabaseSagaSchema<T, TId> sagaStorage)
            {
                return sagaStorage;
            }
        }

        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = new DatabaseSagaSchema<T, TId>(definition, _settings);
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);

        return storage;
    }

    protected override void writeMessageIdArrayQueryList(DbCommandBuilder builder, Guid[] messageIds)
    {
        if (messageIds.Length == 0)
        {
            builder.Append($" AND {DatabaseConstants.Id} IN (NULL)");
            return;
        }

        builder.Append($" AND {DatabaseConstants.Id} IN (");
        for (var i = 0; i < messageIds.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.AppendParameter(messageIds[i]);
        }

        builder.Append(')');
    }

    public override async Task DeleteAllHandledAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(CancellationToken.None);

        var deleted = 1;

        var sql = $@"
DELETE FROM {_settings.SchemaName}.{DatabaseConstants.IncomingTable}
WHERE id IN (
    SELECT id FROM (
        SELECT id
        FROM {_settings.SchemaName}.{DatabaseConstants.IncomingTable}
        WHERE status = '{EnvelopeStatus.Handled}'
        ORDER BY id
        LIMIT 10000
        FOR UPDATE SKIP LOCKED
    ) AS subquery
)";

        try
        {
            while (deleted > 0)
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                deleted = await cmd.ExecuteNonQueryAsync();
                await Task.Delay(10.Milliseconds());
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
