using System.Data;
using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer.Sagas;
using Wolverine.SqlServer.Schema;
using Wolverine.SqlServer.Util;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;
using Table = Weasel.SqlServer.Tables.Table;

namespace Wolverine.SqlServer.Persistence;

public class SqlServerMessageStore : MessageDatabase<SqlConnection>, IDatabaseSagaStorage
{
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _scheduledLockId;
    private ImHashMap<Type, ISagaStorage> _sagaStorage = ImHashMap<Type, ISagaStorage>.Empty;
    
    private readonly List<ISchemaObject> _externalTables = new();
    
    public SqlServerMessageStore(DatabaseSettings database, DurabilitySettings settings,
        ILogger<SqlServerMessageStore> logger, IEnumerable<SagaTableDefinition> sagaTypes)
        : base(database, SqlClientFactory.Instance.CreateDataSource(database.ConnectionString), settings, logger, new SqlServerMigrator(), "dbo")
    {
        _findAtLargeEnvelopesSql =
            $"select top (@limit) {DatabaseConstants.IncomingFields} from {database.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = @address";

        _scheduledLockId = "Wolverine:Scheduled:" + database.ScheduledJobLockId.ToString();
        AdvisoryLock = new AdvisoryLock(() => new SqlConnection(database.ConnectionString),
            logger, Identifier);

        foreach (var sagaTableDefinition in sagaTypes)
        {
            var storage = typeof(SagaStorage<,>).CloseAndBuildAs<ISagaStorage>(sagaTableDefinition, _settings, sagaTableDefinition.SagaType, sagaTableDefinition.IdMember.GetMemberType());
            _sagaStorage = _sagaStorage.AddOrUpdate(sagaTableDefinition.SagaType, storage);
        }
    }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource)
    {
        return new SqlServerNodePersistence(databaseSettings, this);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        return ex is SqlException sqlEx && sqlEx.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint");
    }

    protected override void writePagingAfter(DbCommandBuilder builder, int offset, int limit)
    {
        if (offset == 0) return;
        
        if (offset > 0)
        {
            builder.Append(" OFFSET ");
            builder.AppendParameter(offset);
            builder.Append(" ROWS ");
        }
        
        if (limit > 0)
        {
            builder.Append(" FETCH NEXT ");
            builder.AppendParameter(limit);
            builder.Append(" ROWS ONLY");
        }
    }

    protected override string toTopClause(DeadLetterEnvelopeQuery query)
    {
        if (query.PageSize > 0 && query.PageNumber <= 1)
        {
            return $" top {query.PageSize}";
        }

        return string.Empty;
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using (var reader = await CreateCommand($"select status, count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} group by status")
                         .ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                var count = await reader.GetFieldValueAsync<int>(1);

                switch (status)
                {
                    case EnvelopeStatus.Incoming:
                        counts.Incoming = count;
                        break;

                    case EnvelopeStatus.Handled:
                        counts.Handled = count;
                        break;

                    case EnvelopeStatus.Scheduled:
                        counts.Scheduled = count;
                        break;
                }
            }

            await reader.CloseAsync();
        }

        counts.Outgoing = (int)(await CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync())!;

        counts.DeadLetter = (int)(await CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync())!;

        return counts;
    }

    /// <summary>
    ///     The value of the 'database_principal' parameter in calls to APPLOCK_TEST
    /// </summary>
    public string DatabasePrincipal { get; set; } = "dbo";

    public override Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null)
    {
        var table = new DataTable();
        table.Columns.Add(new DataColumn("ID", typeof(Guid)));
        foreach (var id in ids)
        {
            table.Rows.Add(id);
        }

        var command = CreateCommand($"update {SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Replayable} = @replay where id in (select ID from @IDLIST)");
        command.With("replay", true);
        var list = command.AddNamedParameter("IDLIST", table).As<SqlParameter>();
        list.SqlDbType = SqlDbType.Structured;
        list.TypeName = $"{SchemaName}.EnvelopeIdList";

        return command.ExecuteNonQueryAsync(_cancellation);
    }

    public override Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null)
    {
        var table = new DataTable();
        table.Columns.Add(new DataColumn("ID", typeof(Guid)));
        foreach (var id in ids)
        {
            table.Rows.Add(id);
        }

        var command = CreateCommand($"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where id in (select ID from @IDLIST)");
        var list = command.AddNamedParameter("IDLIST", table).As<SqlParameter>();
        list.SqlDbType = SqlDbType.Structured;
        list.TypeName = $"{SchemaName}.EnvelopeIdList";

        return command.ExecuteNonQueryAsync(_cancellation);
    }

    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Sql Server Envelope Storage in Schema '{SchemaName}'");
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select top {settings.RecoveryBatchSize} {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination";
    }

    public override Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var cmd = CallFunction("uspDiscardAndReassignOutgoing")
            .WithIdList(this, discards, "discards")
            .WithIdList(this, reassigned, "reassigned")
            .With("ownerId", nodeId);

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return Task.CompletedTask;

        return CallFunction("uspDeleteOutgoingEnvelopes")
            .WithIdList(this, envelopes).ExecuteNonQueryAsync(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        return CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override DbCommandBuilder ToCommandBuilder()
    {
        return new DbCommandBuilder(new SqlCommand());
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append( $"select TOP {Durability.RecoveryBatchSize} {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= ");
        builder.AppendParameter(utcNow);
        builder.Append(" order by execution_time");
        builder.Append(';');
    }

    public override async Task MigrateExternalMessageTable(ExternalMessageTable definition)
    {
        var table = (Table)AddExternalMessageTable(definition);
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await table.MigrateAsync(conn);
        await conn.CloseAsync();
    }

    public override async Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName, byte[] json,
        CancellationToken token)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        if (table.MessageTypeColumnName.IsEmpty())
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}) values (@id, @json)")
                .With("id", Guid.NewGuid())
                .With("json", json)
                .ExecuteNonQueryAsync(token);
        }
        else
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) values (@id, @json, @message)")
                .With("id", Guid.NewGuid())
                .With("json", json)
                .With("message", messageTypeName)
                .ExecuteNonQueryAsync(token);
        }
        
        await conn.CloseAsync();
    }

    public override ISchemaObject AddExternalMessageTable(ExternalMessageTable definition)
    {
        var table = new Table(definition.TableName);
        table.AddColumn<Guid>(definition.IdColumnName).AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "varbinary(max)").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn<DateTimeOffset>(definition.TimestampColumnName).DefaultValueByExpression("SYSDATETIMEOFFSET()");
        }

        if (definition.MessageTypeColumnName.IsNotEmpty())
        {
            table.AddColumn(definition.MessageTypeColumnName, "varchar(250)");
        }

        return table;
    }

    protected override Task deleteMany(DbTransaction tx, Guid[] ids, DbObjectName tableName, string idColumnName)
    {
        var builder = new CommandBuilder();

        foreach (var id in ids)
        {
            builder.Append($"delete from {tableName.QualifiedName} where {idColumnName} = ");
            builder.AppendParameter(id);
            builder.Append(";");
        }

        var command = builder.Compile();
        command.Connection = (SqlConnection)tx.Connection;
        command.Transaction = (SqlTransaction)tx;

        return command.ExecuteNonQueryAsync();
    }

    protected override Task<bool> TryAttainLockAsync(int lockId, SqlConnection connection, CancellationToken token)
    {
        return connection.TryGetGlobalLock(lockId.ToString(), token);
    }

    protected override DbCommand buildFetchSql(SqlConnection conn, DbObjectName tableName, string[] columnNames, int maxRecords)
    {
        return conn.CreateCommand($"select top(@limit) {columnNames.Join(", ")} from {tableName.QualifiedName}")
            .With("limit", maxRecords);
    }

    public override async Task PollForScheduledMessagesAsync(ILocalReceiver localQueue,
        ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        if (HasDisposed) return;

        IReadOnlyList<Envelope> envelopes;

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        try
        {
            var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
            if (await tx.TryGetGlobalTxLock(_scheduledLockId, cancellationToken))
            {
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

                var reassign = conn.CreateCommand($"{_settings.SchemaName}.uspMarkIncomingOwnership", tx);
                reassign.CommandType = CommandType.StoredProcedure;

                await reassign
                    .WithIdList(this, envelopes)
                    .With("owner", durabilitySettings.AssignedNodeNumber)
                    .ExecuteNonQueryAsync(_cancellation);

                await tx.CommitAsync(cancellationToken);

                // Judging that there's very little chance of errors here
                foreach (var envelope in envelopes)
                {
                    logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                        envelope.MessageType);
                    localQueue.Enqueue(envelope);
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override DatabaseDescriptor Describe()
    {
        var builder = new SqlConnectionStringBuilder(_settings.ConnectionString);
        var descriptor = new DatabaseDescriptor()
        {
            Engine = "SqlServer",
            ServerName = builder.DataSource ?? string.Empty,
            DatabaseName = builder.InitialCatalog ?? string.Empty,
            Subject = GetType().FullNameInCode(),
            SchemaOrNamespace = _settings.SchemaName
        };

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ApplicationName));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Enlist));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.PersistSecurityInfo));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Pooling));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MinPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CommandTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TrustServerCertificate));

        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("password"));
        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("certificate"));

        return descriptor;
    }

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(SchemaName);
        yield return new IncomingEnvelopeTable(Durability, SchemaName);
        yield return new DeadLettersTable(Durability, SchemaName);
        yield return new EnvelopeIdTable(SchemaName);
        yield return new WolverineStoredProcedure("uspDeleteIncomingEnvelopes.sql", this);
        yield return new WolverineStoredProcedure("uspDeleteOutgoingEnvelopes.sql", this);
        yield return new WolverineStoredProcedure("uspDiscardAndReassignOutgoing.sql", this);
        yield return new WolverineStoredProcedure("uspMarkIncomingOwnership.sql", this);
        yield return new WolverineStoredProcedure("uspMarkOutgoingOwnership.sql", this);
        
        foreach (var table in _externalTables)
        {
            yield return table;
        }
        
        if (_settings.IsMaster)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn<int>("node_number").AutoNumber().NotNull();
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("GETUTCDATE()").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").DefaultValueByExpression("GETUTCDATE()").NotNull();
            nodeTable.AddColumn<string>("version");
            nodeTable.AddColumn("capabilities", "nvarchar(max)").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id").ForeignKeyTo(nodeTable.Identifier, "id", onDelete:CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("GETUTCDATE()").NotNull();

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("GETUTCDATE()");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }

            var eventTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn<int>("id").AutoNumber().AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn<string>("event_name").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("GETUTCDATE()").NotNull();
            eventTable.AddColumn("description", "varchar(500)").AllowNulls();
            yield return eventTable;

            foreach (var entry in _sagaStorage.Enumerate())
            {
                yield return entry.Value.Table;
            }
        }
    }

    public SagaStorage<T, TId> SagaStorageFor<T, TId>() where T : Saga
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is SagaStorage<T, TId> sagaStorage)
            {
                return sagaStorage;
            }
        }
        
        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = new SagaStorage<T, TId>(definition, _settings);
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);
        
        return storage;
    }

    public Task InsertAsync<T>(T saga, DbTransaction transaction, CancellationToken cancellationToken) where T : Saga
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is ISagaStorage<T> sagaStorage)
            {
                return sagaStorage.InsertAsync(saga, transaction, cancellationToken);
            }
        }

        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = typeof(SagaStorage<,>).CloseAndBuildAs<ISagaStorage<T>>(definition, _settings, typeof(T), definition.IdMember.GetMemberType());
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);
        
        return storage.InsertAsync(saga, transaction, cancellationToken);
    }

    public Task UpdateAsync<T>(T saga, DbTransaction transaction, CancellationToken cancellationToken) where T : Saga
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is ISagaStorage<T> sagaStorage)
            {
                return sagaStorage.UpdateAsync(saga, transaction, cancellationToken);
            }
        }

        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = typeof(SagaStorage<,>).CloseAndBuildAs<ISagaStorage<T>>(definition, _settings, typeof(T), definition.IdMember.GetMemberType());
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);
        
        return storage.UpdateAsync(saga, transaction, cancellationToken);
    }

    public Task DeleteAsync<T>(T saga, DbTransaction transaction, CancellationToken cancellationToken) where T : Saga
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is ISagaStorage<T> sagaStorage)
            {
                return sagaStorage.DeleteAsync(saga, transaction, cancellationToken);
            }
        }

        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = typeof(SagaStorage<,>).CloseAndBuildAs<ISagaStorage<T>>(definition, _settings, typeof(T), definition.IdMember.GetMemberType());
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);
        
        return storage.DeleteAsync(saga, transaction, cancellationToken);
    }

    public Task<T?> LoadAsync<T, TId>(TId id, DbTransaction tx, CancellationToken cancellationToken) where T : Saga
    {
        return SagaStorageFor<T, TId>().LoadAsync(id, tx, cancellationToken);
    }
}