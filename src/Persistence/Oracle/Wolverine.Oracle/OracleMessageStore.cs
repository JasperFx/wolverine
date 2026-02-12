using System.Data.Common;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Oracle;
using Weasel.Oracle.Tables;
using Wolverine.Logging;
using Wolverine.Oracle.Sagas;
using Wolverine.Oracle.Schema;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Polling;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;
using Table = Weasel.Oracle.Tables.Table;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore : IMessageDatabase, IMessageInbox, IMessageOutbox, IMessageStoreAdmin, IDeadLetters, ISagaSupport
{
    private readonly OracleDataSource _dataSource;
    private readonly DatabaseSettings _settings;
    private readonly DurabilitySettings _durability;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellation;
    private ImHashMap<Type, IDatabaseSagaSchema> _sagaStorage = ImHashMap<Type, IDatabaseSagaSchema>.Empty;
    private readonly List<Table> _otherTables = new();
    private bool _hasDisposed;
    private string _schemaName;
    private INodeAgentPersistence? _nodes;

    public OracleMessageStore(DatabaseSettings databaseSettings, DurabilitySettings durability,
        OracleDataSource dataSource, ILogger logger)
        : this(databaseSettings, durability, dataSource, logger, Array.Empty<SagaTableDefinition>())
    {
    }

    public OracleMessageStore(DatabaseSettings databaseSettings, DurabilitySettings durability,
        OracleDataSource dataSource, ILogger logger, IEnumerable<SagaTableDefinition> sagaTypes)
    {
        _settings = databaseSettings;
        _durability = durability;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger;
        _cancellation = durability.Cancellation;
        _schemaName = databaseSettings.SchemaName ?? durability.MessageStorageSchemaName ?? "WOLVERINE";
        _schemaName = _schemaName.ToUpperInvariant();

        Role = databaseSettings.Role;
        Settings = databaseSettings;

        AdvisoryLock = new OracleAdvisoryLock(_dataSource, logger, _schemaName);

        foreach (var sagaTableDefinition in sagaTypes)
        {
            var storage = typeof(OracleSagaSchema<,>).CloseAndBuildAs<IDatabaseSagaSchema>(sagaTableDefinition,
                _settings, sagaTableDefinition.SagaType, sagaTableDefinition.IdMember.GetMemberType());
            _sagaStorage = _sagaStorage.AddOrUpdate(sagaTableDefinition.SagaType, storage);
        }

        if (Role == MessageStoreRole.Main)
        {
            _nodes = new OracleNodePersistence(_settings, this, _dataSource);
        }

        var descriptor = Describe();
        var parts = new List<string>
        {
            "oracle",
            descriptor.ServerName.Split(',')[0],
            descriptor.DatabaseName,
            _schemaName
        };

        if (Role == MessageStoreRole.Main)
        {
            Uri = new Uri("wolverine://messages/main");
        }
        else
        {
            Uri = new Uri($"messagedb://{parts.Join("/")}");
        }

        Name = Uri.ToString();
    }

    public OracleDataSource OracleDataSource => _dataSource;
    public DurabilitySettings Durability => _durability;

    // IMessageStore
    public MessageStoreRole Role { get; set; }
    public List<string> TenantIds { get; } = new();
    public Uri Uri { get; internal set; }
    public bool HasDisposed => _hasDisposed;
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public INodeAgentPersistence Nodes => _nodes!;
    public IMessageStoreAdmin Admin => this;
    public IDeadLetters DeadLetters => this;
    public string Name { get; set; }
    public IAdvisoryLock AdvisoryLock { get; }

    // IMessageDatabase
    public DatabaseSettings Settings { get; }
    public string SchemaName
    {
        get => _schemaName;
        set => _schemaName = value.ToUpperInvariant();
    }
    public DbDataSource DataSource => _dataSource;
    public ILogger Logger => _logger;

    public void Initialize(IWolverineRuntime runtime)
    {
        // No-op; initialization happens in MigrateAsync
    }

    public IAgent BuildAgent(IWolverineRuntime runtime)
    {
        return new DurabilityAgent(runtime, this);
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        var agent = new DurabilityAgent(runtime, this);
        agent.StartScheduledJobPolling();
        return agent;
    }

    public DatabaseDescriptor Describe()
    {
        var builder = new OracleConnectionStringBuilder(_dataSource.ConnectionString);
        var descriptor = new DatabaseDescriptor
        {
            Engine = "Oracle",
            ServerName = builder.DataSource ?? string.Empty,
            DatabaseName = _schemaName,
            Subject = GetType().FullNameInCode(),
            SubjectUri = Uri
        };

        descriptor.TenantIds.AddRange(TenantIds);

        return descriptor;
    }

    public Task DrainAsync()
    {
        return Task.CompletedTask;
    }

    public void PromoteToMain(IWolverineRuntime runtime)
    {
        Role = MessageStoreRole.Main;
        Uri = new Uri("wolverine://messages/main");
        _nodes ??= new OracleNodePersistence(_settings, this, _dataSource);
    }

    public void DemoteToAncillary()
    {
        Role = MessageStoreRole.Ancillary;
    }

    public async ValueTask DisposeAsync()
    {
        _hasDisposed = true;
        await AdvisoryLock.DisposeAsync();
    }

    public OracleConnection CreateConnection()
    {
        return _dataSource.CreateConnection();
    }

    public async Task<OracleCommand> CreateCommand(string sql)
    {
        var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(sql);
        return cmd;
    }

    public IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(_durability, SchemaName);
        yield return new IncomingEnvelopeTable(_durability, SchemaName);
        yield return new DeadLettersTable(_durability, SchemaName);
        yield return new LockTable(SchemaName);

        if (Role == MessageStoreRole.Main)
        {
            var nodeTable = new Table(new OracleObjectName(SchemaName, DatabaseConstants.NodeTableName.ToUpperInvariant()));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn("node_number", "NUMBER(10) GENERATED BY DEFAULT AS IDENTITY").NotNull()
                .AddIndex(idx => idx.IsUnique = true);
            nodeTable.AddColumn("description", "VARCHAR2(4000)").NotNull();
            nodeTable.AddColumn("uri", "VARCHAR2(500)").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started")
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").NotNull()
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)");
            nodeTable.AddColumn("version", "VARCHAR2(4000)");
            nodeTable.AddColumn("capabilities", "VARCHAR2(4000)").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new OracleObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName.ToUpperInvariant()));
            assignmentTable.AddColumn("id", "VARCHAR2(500)").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id")
                .ForeignKeyTo(nodeTable.Identifier, "id", onDelete: CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started")
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)").NotNull();

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new OracleObjectName(SchemaName, DatabaseConstants.ControlQueueTableName.ToUpperInvariant()));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn("message_type", "VARCHAR2(4000)").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "BLOB").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull()
                    .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }

            if (_settings.AddTenantLookupTable)
            {
                var tenantTable = new Table(new OracleObjectName(SchemaName, DatabaseConstants.TenantsTableName.ToUpperInvariant()));
                tenantTable.AddColumn("tenant_id", "VARCHAR2(100)").AsPrimaryKey();
                tenantTable.AddColumn("connection_string", "VARCHAR2(500)").NotNull();
                yield return tenantTable;
            }

            var eventTable = new Table(new OracleObjectName(SchemaName, DatabaseConstants.NodeRecordTableName.ToUpperInvariant()));
            eventTable.AddColumn("id", "NUMBER(10) GENERATED BY DEFAULT AS IDENTITY").AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn("event_name", "VARCHAR2(500)").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp")
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)").NotNull();
            eventTable.AddColumn("description", "VARCHAR2(500)").AllowNulls();
            yield return eventTable;

            var restrictionTable = new Table(new OracleObjectName(SchemaName, DatabaseConstants.AgentRestrictionsTableName.ToUpperInvariant()));
            restrictionTable.AddColumn<Guid>("id").AsPrimaryKey();
            restrictionTable.AddColumn("uri", "VARCHAR2(4000)").NotNull();
            restrictionTable.AddColumn("type", "VARCHAR2(4000)").NotNull();
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

    public void AddTable(Table table)
    {
        _otherTables.Add(table);
    }

    public OracleSagaSchema<T, TId> SagaSchemaFor<T, TId>() where T : Saga
    {
        if (_sagaStorage.TryFind(typeof(T), out var raw))
        {
            if (raw is OracleSagaSchema<T, TId> sagaStorage)
            {
                return sagaStorage;
            }
        }

        var definition = new SagaTableDefinition(typeof(T), null);
        var storage = new OracleSagaSchema<T, TId>(definition, _settings);
        _sagaStorage = _sagaStorage.AddOrUpdate(typeof(T), storage);

        return storage;
    }

    // IMessageDatabase - extra methods
    public Weasel.Core.DbCommandBuilder ToCommandBuilder()
    {
        // The IMessageDatabase interface requires DbCommandBuilder, but we create an OracleCommandBuilder
        // internally. Return a DbCommandBuilder that uses Oracle's OracleCommand as the underlying command.
        // Our dead letter methods use ToOracleCommandBuilder() instead.
        return new Weasel.Core.DbCommandBuilder(CreateConnection());
    }

    internal Weasel.Oracle.CommandBuilder ToOracleCommandBuilder()
    {
        return new Weasel.Oracle.CommandBuilder();
    }

    public Task EnqueueAsync(IDatabaseOperation operation)
    {
        // For Oracle, we execute operations directly since we can't batch
        return Task.CompletedTask;
    }

    public void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE status = '{EnvelopeStatus.Scheduled}' AND execution_time <= ");
        builder.AppendParameter(utcNow);
        builder.Append($" ORDER BY execution_time FETCH FIRST {_durability.RecoveryBatchSize} ROWS ONLY");
    }

    public Task PollForMessagesFromExternalTablesAsync(IListener listener, IWolverineRuntime settings,
        ExternalMessageTable externalTable, IReceiver receiver, CancellationToken token)
    {
        // Not implemented yet
        return Task.CompletedTask;
    }

    public async Task MigrateExternalMessageTable(ExternalMessageTable definition)
    {
        var table = AddExternalMessageTable(definition);
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, CancellationToken.None, table);
        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new OracleMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
        }

        await conn.CloseAsync();
    }

    public async Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string messageTypeName,
        byte[] json, CancellationToken token)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        var cmd = conn.CreateCommand("");
        if (table.MessageTypeColumnName.IsEmpty())
        {
            cmd.CommandText =
                $"INSERT INTO {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}) VALUES (:id, :json)";
            cmd.With("id", Guid.NewGuid());
            cmd.Parameters.Add(new OracleParameter("json", OracleDbType.Blob) { Value = json });
        }
        else
        {
            cmd.CommandText =
                $"INSERT INTO {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) VALUES (:id, :json, :message)";
            cmd.With("id", Guid.NewGuid());
            cmd.Parameters.Add(new OracleParameter("json", OracleDbType.Blob) { Value = json });
            cmd.With("message", messageTypeName);
        }

        await cmd.ExecuteNonQueryAsync(token);
        await conn.CloseAsync();
    }

    public ISchemaObject AddExternalMessageTable(ExternalMessageTable definition)
    {
        var table = new Table(definition.TableName);
        table.AddColumn<Guid>(definition.IdColumnName).AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "BLOB").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn<DateTimeOffset>(definition.TimestampColumnName)
                .DefaultValueByExpression("SYS_EXTRACT_UTC(SYSTIMESTAMP)");
        }

        if (definition.MessageTypeColumnName.IsNotEmpty())
        {
            table.AddColumn<string>(definition.MessageTypeColumnName);
        }

        return table;
    }

    // ISagaSupport
    public async ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(MessageContext context)
        where TSaga : Saga
    {
        var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);
        try
        {
            var tx = await conn.BeginTransactionAsync(_cancellation);

            var schema = SagaSchemaFor<TSaga, TId>();

            var transaction = new DatabaseEnvelopeTransaction(this, tx);
            await context.EnlistInOutboxAsync(transaction);
            return new DatabaseSagaStorage<TId, TSaga>(conn, tx, schema);
        }
        catch (Exception)
        {
            await conn.CloseAsync();
            throw;
        }
    }

    // ITenantDatabaseRegistry
    public IDatabaseProvider Provider => OracleProvider.Instance;

    private bool _hasAppliedDefaults;

    public async Task<string> TryFindTenantConnectionString(string tenantId)
    {
        if (!_hasAppliedDefaults && _settings.AutoCreate != AutoCreate.None && _settings.TenantConnections != null)
        {
            await SeedDatabasesAsync(_settings.TenantConnections);
            _hasAppliedDefaults = true;
        }

        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        try
        {
            var cmd = conn.CreateCommand(
                $"SELECT connection_string FROM {SchemaName}.{DatabaseConstants.TenantsTableName} WHERE tenant_id = :id");
            cmd.With("id", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(_cancellation);

            if (await reader.ReadAsync(_cancellation))
            {
                return await reader.GetFieldValueAsync<string>(0, _cancellation);
            }

            await reader.CloseAsync();
            return null!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<Assignment<string>>> LoadAllTenantConnectionStrings()
    {
        if (!_hasAppliedDefaults && _settings.AutoCreate != AutoCreate.None && _settings.TenantConnections != null)
        {
            await SeedDatabasesAsync(_settings.TenantConnections);
            _hasAppliedDefaults = true;
        }

        var list = new List<Assignment<string>>();

        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        try
        {
            var cmd = conn.CreateCommand(
                $"SELECT tenant_id, connection_string FROM {SchemaName}.{DatabaseConstants.TenantsTableName}");
            await using var reader = await cmd.ExecuteReaderAsync(_cancellation);

            while (await reader.ReadAsync(_cancellation))
            {
                var tid = await reader.GetFieldValueAsync<string>(0);
                var cs = await reader.GetFieldValueAsync<string>(1);

                list.Add(new Assignment<string>(tid, cs));
            }

            await reader.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }

        return list;
    }

    public async Task SeedDatabasesAsync(ITenantedSource<string> tenantConnectionStrings)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        try
        {
            foreach (var assignment in tenantConnectionStrings.AllActiveByTenant())
            {
                var deleteCmd = conn.CreateCommand(
                    $"DELETE FROM {SchemaName}.{DatabaseConstants.TenantsTableName} WHERE tenant_id = :tid");
                deleteCmd.With("tid", assignment.TenantId);
                await deleteCmd.ExecuteNonQueryAsync(_cancellation);

                var insertCmd = conn.CreateCommand(
                    $"INSERT INTO {SchemaName}.{DatabaseConstants.TenantsTableName} (tenant_id, connection_string) VALUES (:tid, :cs)");
                insertCmd.With("tid", assignment.TenantId);
                insertCmd.With("cs", assignment.Value);
                await insertCmd.ExecuteNonQueryAsync(_cancellation);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
