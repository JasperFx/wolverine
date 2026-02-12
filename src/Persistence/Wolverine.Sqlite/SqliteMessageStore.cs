using System.Data.Common;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Sqlite.Schema;
using Wolverine.Sqlite.Sagas;
using Wolverine.Sqlite.Util;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.Sqlite;

internal class SqliteMessageStore : MessageDatabase<SqliteConnection>
{
    private readonly string _deleteOutgoingEnvelopesSql;
    private readonly string _discardAndReassignOutgoingSql;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _reassignIncomingSql;

    private readonly List<ISchemaObject> _externalTables = new();

    private ImHashMap<Type, IDatabaseSagaSchema> _sagaStorage = ImHashMap<Type, IDatabaseSagaSchema>.Empty;

    public SqliteMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, DbDataSource dataSource,
        ILogger<SqliteMessageStore> logger) : this(databaseSettings, settings, dataSource, logger, Array.Empty<SagaTableDefinition>())
    {
        var descriptor = Describe();
        Id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
    }

    public SqliteMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, DbDataSource dataSource,
        ILogger<SqliteMessageStore> logger, IEnumerable<SagaTableDefinition> sagaTypes) : base(databaseSettings, dataSource,
        settings, logger, new SqliteMigrator(), SqliteProvider.Instance)
    {
        _reassignIncomingSql =
            $"update {DatabaseConstants.IncomingTable} set owner_id = @owner, status = '{EnvelopeStatus.Incoming}' where id IN (@ids)";
        _deleteOutgoingEnvelopesSql =
            $"delete from {DatabaseConstants.OutgoingTable} WHERE id IN (@ids);";

        _findAtLargeEnvelopesSql =
            $"select {DatabaseConstants.IncomingFields} from {DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = @address limit @limit";

        _discardAndReassignOutgoingSql = _deleteOutgoingEnvelopesSql +
                                         $";update {DatabaseConstants.OutgoingTable} set owner_id = @node where id IN (@rids)";

        DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        AdvisoryLock = new SqliteAdvisoryLock(dataSource, logger, Identifier);

        foreach (var sagaTableDefinition in sagaTypes)
        {
            var storage = typeof(DatabaseSagaSchema<,>).CloseAndBuildAs<IDatabaseSagaSchema>(sagaTableDefinition, _settings, sagaTableDefinition.SagaType, sagaTableDefinition.IdMember.GetMemberType());
            _sagaStorage = _sagaStorage.AddOrUpdate(sagaTableDefinition.SagaType, storage);
        }
    }

    public DbDataSource DataSource { get; }

    public async Task<IReadOnlyList<DbObjectName>> SchemaTablesAsync(CancellationToken ct = default)
    {
        var schemaNames = AllSchemaNames();

        await using var conn = (SqliteConnection)await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        return await conn.ExistingTablesAsync(schemas: schemaNames, ct: ct).ConfigureAwait(false);
    }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource)
    {
        return new SqliteNodePersistence(databaseSettings, this, dataSource);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        if (ex is SqliteException sqliteException)
        {
            return sqliteException.SqliteErrorCode == 19 || // SQLITE_CONSTRAINT
                   sqliteException.Message.Contains("UNIQUE constraint failed");
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
        var table = new Weasel.Sqlite.Tables.Table(definition.TableName);
        table.AddColumn(definition.IdColumnName, "TEXT").AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "TEXT").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn(definition.TimestampColumnName, "TEXT").DefaultValueByExpression("(datetime('now'))");
        }

        if (definition.MessageTypeColumnName.IsNotEmpty())
        {
            table.AddColumn(definition.MessageTypeColumnName, "TEXT");
        }

        return table;
    }

    public override async Task MigrateExternalMessageTable(ExternalMessageTable definition)
    {
        var table = (Weasel.Sqlite.Tables.Table)AddExternalMessageTable(definition);
        await using var conn = (SqliteConnection)await DataSource.OpenConnectionAsync().ConfigureAwait(false);

        var migration = await SchemaMigration.DetermineAsync(conn, default(CancellationToken), table);
        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new SqliteMigrator().ApplyAllAsync(conn, migration, _settings.AutoCreate);
        }

        await conn.CloseAsync();
    }

    protected override Task deleteMany(DbTransaction tx, Guid[] ids, DbObjectName tableName,
        string idColumnName)
    {
        var idList = string.Join(",", ids.Select(id => $"'{id.ToString().ToUpperInvariant()}'"));
        return tx.CreateCommand($"delete from {tableName.QualifiedName} where {idColumnName} IN ({idList})")
            .ExecuteNonQueryAsync();
    }

    protected override async Task<bool> TryAttainLockAsync(int lockId, SqliteConnection connection, CancellationToken token)
    {
        // SQLite uses BEGIN EXCLUSIVE TRANSACTION for locking
        // We'll use a simple advisory lock table approach
        try
        {
            await connection.CreateCommand($"INSERT OR IGNORE INTO wolverine_locks (lock_id, acquired_at) VALUES ({lockId}, datetime('now'))")
                .ExecuteNonQueryAsync(token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override DbCommand buildFetchSql(SqliteConnection conn, DbObjectName tableName, string[] columnNames, int maxRecords)
    {
        return conn.CreateCommand($"select {columnNames.Join(", ")} from {tableName.QualifiedName} LIMIT {maxRecords}");
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using (var reader = await CreateCommand($"select status, count(*) from {DatabaseConstants.IncomingTable} group by status")
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

        var longCount = await CreateCommand($"select count(*) from {DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync();

        counts.Outgoing = Convert.ToInt32(longCount);

        var deadLetterCount = await CreateCommand($"select count(*) from {DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync();

        counts.DeadLetter = Convert.ToInt32(deadLetterCount);

        return counts;
    }

    public override async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var discardIds = string.Join(",", discards.Select(e => $"'{e.Id.ToString().ToUpperInvariant()}'"));
        var reassignIds = string.Join(",", reassigned.Select(e => $"'{e.Id.ToString().ToUpperInvariant()}'"));

        await using var cmd = CreateCommand(
            $"delete from {DatabaseConstants.OutgoingTable} WHERE id IN ({discardIds});" +
            $"update {DatabaseConstants.OutgoingTable} set owner_id = @node where id IN ({reassignIds})");
        cmd.Parameters.Add(new SqliteParameter("@node", nodeId));

        await cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return;

        var ids = string.Join(",", envelopes.Select(e => $"'{e.Id.ToString().ToUpperInvariant()}'"));
        await CreateCommand($"delete from {DatabaseConstants.OutgoingTable} WHERE id IN ({ids})")
            .ExecuteNonQueryAsync(_cancellation);
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select {DatabaseConstants.OutgoingFields} from {DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination LIMIT {settings.RecoveryBatchSize}";
    }

    public override async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress,
        int limit)
    {
        return await CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override DbCommandBuilder ToCommandBuilder()
    {
        return new DbCommandBuilder(new SqliteCommand());
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= ");

        builder.AppendParameter(utcNow.ToString("O"));
        builder.Append($" order by execution_time LIMIT {Durability.RecoveryBatchSize};");
    }

    public override async Task PollForScheduledMessagesAsync(IWolverineRuntime runtime, ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        IReadOnlyList<Envelope> envelopes;

        if (HasDisposed) return;

        await using var conn = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var tx = await conn.BeginTransactionAsync(cancellationToken);

            var builder = new DbCommandBuilder(conn);
            WriteLoadScheduledEnvelopeSql(builder, DateTimeOffset.UtcNow);
            var cmd = builder.Compile();
            cmd.Connection = conn;
            cmd.Transaction = tx;

            envelopes = await cmd.FetchListAsync(reader =>
                DatabasePersistence.ReadIncomingAsync(reader, cancellationToken), cancellation: cancellationToken);

            if (!envelopes.Any())
            {
                await tx.RollbackAsync(cancellationToken);
                return;
            }

            var ids = string.Join(",", envelopes.Select(e => $"'{e.Id.ToString().ToUpperInvariant()}'"));
            await conn.CreateCommand(
                    $"update {DatabaseConstants.IncomingTable} set owner_id = @owner, status = '{EnvelopeStatus.Incoming}' where id IN ({ids})")
                .With("owner", durabilitySettings.AssignedNodeNumber)
                .ExecuteNonQueryAsync(_cancellation);

            await tx.CommitAsync(cancellationToken);

            await runtime.EnqueueDirectlyAsync(envelopes);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (HasDisposed) return false;

        await using var conn = await DataSource.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        if (Durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            var count = await conn
                .CreateCommand($"select count(id) from {DatabaseConstants.IncomingTable} where id = @id")
                .With("id", envelope.Id)
                .ExecuteScalarAsync(cancellation);

            return Convert.ToInt64(count) > 0;
        }
        else
        {
            var count = await conn
                .CreateCommand($"select count(id) from {DatabaseConstants.IncomingTable} where id = @id and {DatabaseConstants.ReceivedAt} = @destination")
                .With("id", envelope.Id)
                .With("destination", envelope.Destination!.ToString())
                .ExecuteScalarAsync(cancellation);

            return Convert.ToInt64(count) > 0;
        }
    }

    public override async Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName, byte[] json,
        CancellationToken token)
    {
        await using var conn = await DataSource.OpenConnectionAsync(token).ConfigureAwait(false);

        if (table.MessageTypeColumnName.IsEmpty())
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}) values (@id, @json)")
                .With("id", Guid.NewGuid().ToString())
                .With("json", System.Text.Encoding.UTF8.GetString(json))
                .ExecuteNonQueryAsync(token);
        }
        else
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) values (@id, @json, @message)")
                .With("id", Guid.NewGuid().ToString())
                .With("json", System.Text.Encoding.UTF8.GetString(json))
                .With("message", messageTypeName)
                .ExecuteNonQueryAsync(token);
        }

        await conn.CloseAsync();
    }

    public override DatabaseDescriptor Describe()
    {
        var builder = new SqliteConnectionStringBuilder(DataSource?.ConnectionString ?? Settings.ConnectionString);
        var descriptor = new DatabaseDescriptor()
        {
            Engine = "SQLite",
            ServerName = "local",
            DatabaseName = builder.DataSource ?? ":memory:",
            Subject = GetType().FullNameInCode(),
            SubjectUri = SubjectUri,
            Identifier = Identifier
        };

        descriptor.TenantIds.AddRange(TenantIds);

        descriptor.Properties.Add(new OptionsValue { Name = "DataSource", Value = builder.DataSource });
        descriptor.Properties.Add(new OptionsValue { Name = "Mode", Value = builder.Mode.ToString() });
        descriptor.Properties.Add(new OptionsValue { Name = "Cache", Value = builder.Cache.ToString() });

        return descriptor;
    }

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(Durability, SchemaName);
        yield return new IncomingEnvelopeTable(Durability, SchemaName);
        yield return new DeadLettersTable(Durability, SchemaName);

        foreach (var table in _externalTables)
        {
            yield return table;
        }

        if (Role == MessageStoreRole.Main)
        {
            var nodeTable = new Weasel.Sqlite.Tables.Table(new SqliteObjectName(DatabaseConstants.NodeTableName));
            nodeTable.AddColumn("id", "TEXT").AsPrimaryKey();
            nodeTable.AddColumn("node_number", "INTEGER").NotNull();
            nodeTable.AddColumn("description", "TEXT").NotNull();
            nodeTable.AddColumn("uri", "TEXT").NotNull();
            nodeTable.AddColumn("started", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");
            nodeTable.AddColumn("health_check", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");
            nodeTable.AddColumn("version", "TEXT");
            nodeTable.AddColumn("capabilities", "TEXT");

            yield return nodeTable;

            var assignmentTable = new Weasel.Sqlite.Tables.Table(new SqliteObjectName(DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn("id", "TEXT").AsPrimaryKey();
            assignmentTable.AddColumn("node_id", "TEXT").NotNull();
            assignmentTable.AddColumn("started", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Weasel.Sqlite.Tables.Table(new SqliteObjectName(DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn("id", "TEXT").AsPrimaryKey();
                queueTable.AddColumn("message_type", "TEXT").NotNull();
                queueTable.AddColumn("node_id", "TEXT").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
                queueTable.AddColumn("posted", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");
                queueTable.AddColumn("expires", "TEXT");

                yield return queueTable;
            }

            if (_settings.AddTenantLookupTable)
            {
                var tenantTable = new Weasel.Sqlite.Tables.Table(new SqliteObjectName(DatabaseConstants.TenantsTableName));
                tenantTable.AddColumn(StorageConstants.TenantIdColumn, "TEXT").AsPrimaryKey();
                tenantTable.AddColumn(StorageConstants.ConnectionStringColumn, "TEXT").NotNull();
                yield return tenantTable;
            }

            var eventTable = new Weasel.Sqlite.Tables.Table(new SqliteObjectName(DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn("id", "INTEGER").AsPrimaryKey().AutoIncrement();
            eventTable.AddColumn("node_number", "INTEGER").NotNull();
            eventTable.AddColumn("event_name", "TEXT").NotNull();
            eventTable.AddColumn("timestamp", "TEXT").NotNull().DefaultValueByExpression("(datetime('now'))");
            eventTable.AddColumn("description", "TEXT");
            yield return eventTable;

            var restrictionTable =
                new Weasel.Sqlite.Tables.Table(new SqliteObjectName(DatabaseConstants.AgentRestrictionsTableName));
            restrictionTable.AddColumn("id", "TEXT").AsPrimaryKey();
            restrictionTable.AddColumn("uri", "TEXT").NotNull();
            restrictionTable.AddColumn("type", "TEXT").NotNull();
            restrictionTable.AddColumn("node", "INTEGER").NotNull().DefaultValue(0);
            yield return restrictionTable;

            // Advisory lock table for SQLite
            var lockTable = new Weasel.Sqlite.Tables.Table(new SqliteObjectName("wolverine_locks"));
            lockTable.AddColumn("lock_id", "INTEGER").AsPrimaryKey();
            lockTable.AddColumn("acquired_at", "TEXT").NotNull();
            yield return lockTable;
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

    private readonly List<Weasel.Sqlite.Tables.Table> _otherTables = new();

    public void AddTable(Weasel.Sqlite.Tables.Table table)
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
        var ids = string.Join(",", messageIds.Select(id => $"'{id.ToString().ToUpperInvariant()}'"));
        builder.Append($" and {DatabaseConstants.Id} IN ({ids})");
    }

    public override async Task DeleteAllHandledAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync().ConfigureAwait(false);

        var deleted = 1;

        var sql = $@"
        DELETE FROM {DatabaseConstants.IncomingTable}
        WHERE id IN (
            SELECT id FROM {DatabaseConstants.IncomingTable}
            WHERE status = '{EnvelopeStatus.Handled}'
            LIMIT 10000
        );
";

        try
        {
            while (deleted > 0)
            {
                deleted = await conn.CreateCommand(sql).ExecuteNonQueryAsync();
                await Task.Delay(10);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
