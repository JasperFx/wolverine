using System.Data;
using System.Data.Common;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS.Polling;
using Wolverine.RDBMS.Sagas;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : DatabaseBase<T>,
    IMessageDatabase, IMessageInbox, IMessageOutbox, IMessageStoreAdmin, IDeadLetters, ISagaSupport where T : DbConnection, new()
{
    // ReSharper disable once InconsistentNaming
    protected readonly CancellationToken _cancellation;
    private readonly string _outgoingEnvelopeSql;
    protected readonly DatabaseSettings _settings;
    private readonly DbDataSource _dataSource;
    private DatabaseBatcher? _batcher;
    private string _schemaName;

    protected MessageDatabase(DatabaseSettings databaseSettings, DbDataSource dataSource, DurabilitySettings settings,
        ILogger logger, Migrator migrator, IDatabaseProvider provider) : base(new MigrationLogger(logger),
        databaseSettings.AutoCreate, migrator,
        "WolverineEnvelopeStorage", () => (T)dataSource.CreateConnection())
    {
        Provider = provider;
        _settings = databaseSettings;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        Logger = logger;
        _schemaName = databaseSettings.SchemaName ?? settings.MessageStorageSchemaName ?? provider.DefaultDatabaseSchemaName;

        IncomingFullName = $"{SchemaName}.{DatabaseConstants.IncomingTable}";
        OutgoingFullName = $"{SchemaName}.{DatabaseConstants.OutgoingTable}";

        Durability = settings;
        _cancellation = settings.Cancellation;

        _markEnvelopeAsHandledById =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = @id and {DatabaseConstants.ReceivedAt} = @uri";
        _incrementIncomingEnvelopeAttempts =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set attempts = @attempts where id = @id and {DatabaseConstants.ReceivedAt} = @uri";

        // ReSharper disable once VirtualMemberCallInConstructor
        _outgoingEnvelopeSql = determineOutgoingEnvelopeSql(settings);

        // ReSharper disable once VirtualMemberCallInConstructor
        Nodes = buildNodeStorage(databaseSettings, dataSource)!;

        DataSource = dataSource;

        var descriptor = Describe();
        
        var parts = new List<string>
        {
            descriptor.Engine.ToLowerInvariant(),
            descriptor.ServerName,
            descriptor.DatabaseName,
            _schemaName
        };

        if (databaseSettings.IsMain)
        {
            SubjectUri = new Uri("wolverine://messages/main");
        }

        Uri = new Uri($"{PersistenceConstants.AgentScheme}://{parts.Where(x => x.IsNotEmpty()).Join("/")}");
    }

    public override string ToString()
    {
        return $"{Uri} ({Name})";
    }

    public IAgent BuildAgent(IWolverineRuntime runtime)
    {
        return new DurabilityAgent(Name, runtime, this);
    }

    public Uri Uri { get; protected set; } = new Uri("null://null");
    
    public IAdvisoryLock AdvisoryLock { get; protected set; }

    public ILogger Logger { get; }

    public bool HasDisposed { get; protected set; }

    public DbDataSource DataSource { get; }

    public string OutgoingFullName { get; private set; }

    public string IncomingFullName { get; private set; }

    public DurabilitySettings Durability { get; }

    public bool IsMain => Settings.IsMain;

    public string Name { get; set; } = TransportConstants.Default;

    public DatabaseSettings Settings => _settings;

    public INodeAgentPersistence Nodes { get; }

    public IMessageInbox Inbox => this;

    public IMessageOutbox Outbox => this;
    public IDeadLetters DeadLetters => this;

    public string SchemaName
    {
        get => _schemaName;
        set
        {
            _schemaName = value;

            IncomingFullName = $"{value}.{DatabaseConstants.IncomingTable}";
            OutgoingFullName = $"{value}.{DatabaseConstants.OutgoingTable}";
        }
    }

    public Task EnqueueAsync(IDatabaseOperation operation)
    {
        // Really probably only an issue w/ testing, but this lets us ignore 
        // log record saving
        if (!Durability.DurabilityAgentEnabled) return Task.CompletedTask;
        
        if (_batcher == null)
        {
            throw new InvalidOperationException($"Message database '{Identifier}' has not yet been initialized for node {Durability.AssignedNodeNumber}");
        }

        return _batcher.EnqueueAsync(operation);
    }

    public abstract Task PollForScheduledMessagesAsync(ILocalReceiver localQueue, ILogger runtimeLogger,
        DurabilitySettings durabilitySettings,
        CancellationToken cancellationToken);

    public void Initialize(IWolverineRuntime runtime)
    {
        if (_batcher != null) return;

        _batcher = new DatabaseBatcher(this, runtime, runtime.Options.Durability.Cancellation);

        if (Settings.IsMain && runtime.Options.Transports.NodeControlEndpoint == null && runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            var transport = new DatabaseControlTransport(this, runtime.Options);
            runtime.Options.Transports.Add(transport);

            runtime.Options.Transports.NodeControlEndpoint = transport.ControlEndpoint;
        }
    }

    public Uri SubjectUri { get; set; } = new Uri("wolverine://messages");

    public IMessageStoreAdmin Admin => this;

    public abstract void Describe(TextWriter writer);

    public async Task DrainAsync()
    {
        if (_batcher != null)
        {
            try
            {
                await _batcher!.DrainAsync();
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (HasDisposed) return;

        if (AdvisoryLock != null)
        {
            await AdvisoryLock.DisposeAsync();
        }

        if (_batcher != null)
        {
            await _batcher.DisposeAsync();
        }

        try
        {
            await DataSource.DisposeAsync();
        }
        catch
        {
            // Not letting this fail out of here
        }

        HasDisposed = true;
    }

    public abstract DbCommandBuilder ToCommandBuilder();

    public async Task ReleaseIncomingAsync(int ownerId)
    {
        if (_cancellation.IsCancellationRequested) return;

        var count = await _dataSource
            .CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @owner")
            .With("owner", ownerId)
            .ExecuteNonQueryAsync(_cancellation);
        
        Logger.LogInformation("Reassigned {Count} incoming messages from {Owner} to any node in the durable inbox", count, ownerId);
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        if (HasDisposed) return;

        var impacted = await _dataSource
            .CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @owner and {DatabaseConstants.ReceivedAt} = @uri")
            .With("owner", ownerId)
            .With("uri", receivedAt.ToString())
            .ExecuteNonQueryAsync(_cancellation);

        if (impacted == 0) return;

        Logger.LogInformation("Reassigned {Impacted} incoming messages from {Owner} and endpoint at {Uri} to any node in the durable inbox", impacted, ownerId, receivedAt);
    }

    protected abstract INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource);

    public DbCommand CreateCommand(string command)
    {
        return _dataSource.CreateCommand(command);
    }

    public DbCommand CallFunction(string functionName)
    {
        var cmd = CreateCommand(SchemaName + "." + functionName);
        cmd.CommandType = CommandType.StoredProcedure;
        return cmd;
    }

    public new abstract IEnumerable<ISchemaObject> AllObjects();

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        var agent = new DurabilityAgent(TransportConstants.Default, runtime, this);
        agent.StartScheduledJobPolling();

        return agent;
    }

    public IAgentFamily? BuildAgentFamily(IWolverineRuntime runtime)
    {
        return new DurabilityAgentFamily(runtime);
    }

    public async ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(MessageContext context) where TSaga : Saga
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

    public abstract IDatabaseSagaSchema<TId, TSaga> SagaSchemaFor<TSaga, TId>() where TSaga : Saga;
}