using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql.Schema;
using Wolverine.Postgresql.Util;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;
using Table = Weasel.Postgresql.Tables.Table;

namespace Wolverine.Postgresql;

/// <summary>
/// Built to work with separate Marten stores
/// </summary>
/// <typeparam name="T"></typeparam>
internal class PostgresqlMessageStore<T> : PostgresqlMessageStore, IAncillaryMessageStore<T>
{
    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, NpgsqlDataSource dataSource, ILogger<PostgresqlMessageStore> logger) : base(databaseSettings, settings, dataSource, logger)
    {

    }

    public Type MarkerType => typeof(T);
}

internal class PostgresqlMessageStore : MessageDatabase<NpgsqlConnection>, IDatabaseSagaStorage
{
    private readonly string _deleteOutgoingEnvelopesSql;
    private readonly string _discardAndReassignOutgoingSql;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _reassignIncomingSql;

    private readonly List<ISchemaObject> _externalTables = new();
    
    private ImHashMap<Type, ISagaStorage> _sagaStorage = ImHashMap<Type, ISagaStorage>.Empty;


    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, NpgsqlDataSource dataSource,
        ILogger<PostgresqlMessageStore> logger) : this(databaseSettings, settings, GetPrimaryNpgsqlNodeIfPossible(dataSource), logger, Array.Empty<SagaTableDefinition>())
    {
    }

    private static NpgsqlDataSource GetPrimaryNpgsqlNodeIfPossible(NpgsqlDataSource dataSource)
    {
        if (dataSource is NpgsqlMultiHostDataSource multiHost)
            return multiHost.WithTargetSession(TargetSessionAttributes.Primary);
        return dataSource;
    }

    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings, NpgsqlDataSource dataSource,
        ILogger<PostgresqlMessageStore> logger, IEnumerable<SagaTableDefinition> sagaTypes) : base(databaseSettings, dataSource,
        settings, logger, new PostgresqlMigrator(), "public")
    {
        _reassignIncomingSql =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = @owner, status = '{EnvelopeStatus.Incoming}' where id = ANY(@ids)";
        _deleteOutgoingEnvelopesSql =
            $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = ANY(@ids);";

        _findAtLargeEnvelopesSql =
            $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = :address limit :limit";

        _discardAndReassignOutgoingSql = _deleteOutgoingEnvelopesSql +
                                         $";update {SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @node where id = ANY(@rids)";

        NpgsqlDataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        AdvisoryLock = new AdvisoryLock(dataSource, logger, Identifier);
        
        foreach (var sagaTableDefinition in sagaTypes)
        {
            var storage = typeof(SagaStorage<,>).CloseAndBuildAs<ISagaStorage>(sagaTableDefinition, _settings, sagaTableDefinition.SagaType, sagaTableDefinition.IdMember.GetMemberType());
            _sagaStorage = _sagaStorage.AddOrUpdate(sagaTableDefinition.SagaType, storage);
        }
    }

    public NpgsqlDataSource NpgsqlDataSource { get; }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource)
    {
        return new PostgresqlNodePersistence(databaseSettings, this, (NpgsqlDataSource)dataSource);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        if (ex is PostgresException postgresException)
        {
            return
                postgresException.Message.Contains("duplicate key value violates unique constraint");
        }

        return false;
    }

    protected override void writePagingAfter(DbCommandBuilder builder, int offset, int limit)
    {
        if (offset > 0)
        {
            builder.Append(" OFFSET ");
            builder.AppendParameter(offset);
        }
        
        if (limit > 0)
        {
            builder.Append(" LIMIT ");
            builder.AppendParameter(limit);
        }
    }

    public override ISchemaObject AddExternalMessageTable(ExternalMessageTable definition)
    {
        var table = new Table(definition.TableName);
        table.AddColumn<Guid>(definition.IdColumnName).AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "jsonb").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn<DateTimeOffset>(definition.TimestampColumnName).DefaultValueByExpression("((now() at time zone 'utc'))");
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
        await table.MigrateAsync(conn);
        await conn.CloseAsync();
    }

    protected override Task deleteMany(DbTransaction tx, Guid[] ids, DbObjectName tableName,
        string idColumnName)
    {
        return tx.CreateCommand($"delete from {tableName.QualifiedName} where {idColumnName} = ANY(@ids)")
            .As<NpgsqlCommand>().With("ids", ids).ExecuteNonQueryAsync();

    }

    protected override async Task<bool> TryAttainLockAsync(int lockId, NpgsqlConnection connection, CancellationToken token)
    {
        return await connection.TryGetGlobalLock(lockId, cancellation: token) == AttainLockResult.Success;
    }

    protected override DbCommand buildFetchSql(NpgsqlConnection conn, DbObjectName tableName, string[] columnNames, int maxRecords)
    {
        return conn.CreateCommand($"select {columnNames.Join(", ")} from {tableName.QualifiedName} LIMIT :limit")
            .With("limit", maxRecords);
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using (var reader = await CreateCommand( $"select status, count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} group by status")
                         .ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                var count = await reader.GetFieldValueAsync<int>(1);

                if (status == EnvelopeStatus.Incoming)
                {
                    counts.Incoming = count;
                }
                else if (status == EnvelopeStatus.Handled)
                {
                    counts.Handled = count;
                }
                else if (status == EnvelopeStatus.Scheduled)
                {
                    counts.Scheduled = count;
                }
            }

            await reader.CloseAsync();
        }

        var longCount = await CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync();

        counts.Outgoing = Convert.ToInt32(longCount);

        var deadLetterCount = await CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync();

        counts.DeadLetter = Convert.ToInt32(deadLetterCount);

        return counts;
    }
    
    public override async Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null)
    {
        var builder = ToCommandBuilder();
        builder.Append($"update {SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Replayable} = @replay where id = ANY(@ids)");

        var cmd = builder.Compile();
        cmd.With("replay", true);
        var param = new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };
        cmd.Parameters.Add(param);
        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(_cancellation);
        cmd.Connection = conn;
        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override async Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null)
    {
        var builder = ToCommandBuilder();
        builder.Append($"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where id = ANY(@ids)");

        var cmd = builder.Compile();
        var param = new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };
        cmd.Parameters.Add(param);
        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(_cancellation);
        cmd.Connection = conn;
        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Persistent Envelope storage using Postgresql in schema '{SchemaName}'");
    }

    public override async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        await using var cmd = CreateCommand(_discardAndReassignOutgoingSql)
            .WithEnvelopeIds("ids", discards)
            .With("node", nodeId)
            .WithEnvelopeIds("rids", reassigned);

        await cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return;

        await CreateCommand(_deleteOutgoingEnvelopesSql)
            .WithEnvelopeIds("ids", envelopes)
            .ExecuteNonQueryAsync(_cancellation);
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination LIMIT {settings.RecoveryBatchSize}";
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
        return new DbCommandBuilder(new NpgsqlCommand());
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= ");

        builder.AppendParameter(utcNow);
        builder.Append($" order by execution_time LIMIT {Durability.RecoveryBatchSize};");
    }

    public override async Task PollForScheduledMessagesAsync(ILocalReceiver localQueue, ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        IReadOnlyList<Envelope> envelopes;

        if (HasDisposed) return;

        await using var conn = await NpgsqlDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var tx = await conn.BeginTransactionAsync(cancellationToken);
            if (await tx.TryGetGlobalTxLock(Settings.ScheduledJobLockId, cancellationToken) == AttainLockResult.Success)
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

                await conn.CreateCommand(_reassignIncomingSql)
                    .With("owner", durabilitySettings.AssignedNodeNumber)
                    .With("ids", envelopes.Select(x => x.Id).ToArray())
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
                .With("json", json, NpgsqlDbType.Jsonb)
                .ExecuteNonQueryAsync(token);
        }
        else
        {
            await conn.CreateCommand(
                    $"insert into {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) values (@id, @json, @message)")
                .With("id", Guid.NewGuid())
                .With("json", json, NpgsqlDbType.Jsonb)
                .With("message", messageTypeName)
                .ExecuteNonQueryAsync(token);
        }
        
        await conn.CloseAsync();
    }

    public override DatabaseDescriptor Describe()
    {
        if (Descriptor != null) return Descriptor;
        
        var builder = new NpgsqlConnectionStringBuilder(DataSource?.ConnectionString ?? Settings.ConnectionString);
        var descriptor = new DatabaseDescriptor()
        {
            Engine = "PostgreSQL",
            ServerName = builder.Host ?? string.Empty,
            DatabaseName = builder.Database ?? string.Empty,
            Subject = GetType().FullNameInCode()
        };

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Host));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Port));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Database));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Username));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ApplicationName));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Enlist));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SearchPath));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ClientEncoding));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Encoding));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Timezone));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SslMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SslNegotiation));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CheckCertificateRevocation));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.KerberosServiceName));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.IncludeRealm));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.PersistSecurityInfo));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.LogParameters));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.IncludeErrorDetail));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ChannelBinding));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Pooling));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MinPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionIdleLifetime));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionPruningInterval));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ConnectionLifetime));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Timeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CommandTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CancellationTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TargetSessionAttributes));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.LoadBalanceHosts));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.HostRecheckSeconds));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.KeepAlive));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TcpKeepAlive));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TcpKeepAliveTime));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TcpKeepAliveInterval));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ReadBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.WriteBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SocketReceiveBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.SocketSendBufferSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxAutoPrepare));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.AutoPrepareMinUsages));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.NoResetOnClose));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Options));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ArrayNullabilityMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Multiplexing));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.WriteCoalescingBufferThresholdBytes));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.LoadTableComposites));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ServerCompatibilityMode));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TrustServerCertificate));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.InternalCommandTimeout));

        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("password"));
        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("certificate"));

        return descriptor;
    }

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(SchemaName);
        yield return new IncomingEnvelopeTable(Durability, SchemaName);
        yield return new DeadLettersTable(Durability, SchemaName);

        foreach (var table in _externalTables)
        {
            yield return table;
        }

        if (_settings.IsMaster)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn("node_number", "SERIAL").NotNull();
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("now()").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").NotNull().DefaultValueByExpression("now()");
            nodeTable.AddColumn<string>("version");
            nodeTable.AddColumn("capabilities", "text[]").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id")
                .ForeignKeyTo(nodeTable.Identifier, "id", onDelete: CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("now()").NotNull();

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "bytea").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("NOW()");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }

            var eventTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn("id", "SERIAL").AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn<string>("event_name").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()").NotNull();
            eventTable.AddColumn<string>("description").AllowNulls();
            yield return eventTable;

            foreach (var table in _otherTables)
            {
                yield return table;
            }
            
            foreach (var entry in _sagaStorage.Enumerate())
            {
                yield return entry.Value.Table;
            }
        }
    }

    private readonly List<Table> _otherTables = new();

    public void AddTable(Table table)
    {
        _otherTables.Add(table);
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