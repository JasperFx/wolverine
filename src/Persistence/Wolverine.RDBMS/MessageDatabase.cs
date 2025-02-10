using System.Data;
using System.Data.Common;
using JasperFx.Core.Descriptions;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : DatabaseBase<T>,
    IMessageDatabase, IMessageInbox, IMessageOutbox, IMessageStoreAdmin, IDeadLetters where T : DbConnection, new()
{
    // ReSharper disable once InconsistentNaming
    protected readonly CancellationToken _cancellation;
    private readonly string _outgoingEnvelopeSql;
    protected readonly DatabaseSettings _settings;
    private readonly DbDataSource _dataSource;
    private DatabaseBatcher? _batcher;
    private string _schemaName;

    protected MessageDatabase(DatabaseSettings databaseSettings, DbDataSource dataSource, DurabilitySettings settings,
        ILogger logger, Migrator migrator, string defaultSchema) : base(new MigrationLogger(logger),
        databaseSettings.AutoCreate, migrator,
        "WolverineEnvelopeStorage", () => (T)dataSource.CreateConnection())
    {
        _settings = databaseSettings;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        Logger = logger;
        _schemaName = databaseSettings.SchemaName ?? defaultSchema;

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
    }
    
    // This would be set from the parent database, if one exists. Example would be from
    // gleaning Wolverine message storage off of Marten storage databases
    public DatabaseDescriptor? Descriptor { get; set; }

    public IAdvisoryLock AdvisoryLock { get; protected set; }

    public ILogger Logger { get; }

    public bool HasDisposed { get; protected set; }

    public DbDataSource DataSource { get; }

    public string OutgoingFullName { get; private set; }

    public string IncomingFullName { get; private set; }

    public DurabilitySettings Durability { get; }

    public bool IsMaster => Settings.IsMaster;

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
        if (_batcher == null)
        {
            throw new InvalidOperationException($"Message database '{Identifier}' has not yet been initialized for node {Durability.AssignedNodeNumber}");
        }

        return _batcher.EnqueueAsync(operation);
    }

    public void Enqueue(IDatabaseOperation operation)
    {
        if (_batcher == null)
        {
            throw new InvalidOperationException($"Message database '{Identifier}' has not yet been initialized");
        }

        _batcher.Enqueue(operation);
    }

    public abstract Task PollForScheduledMessagesAsync(ILocalReceiver localQueue, ILogger runtimeLogger,
        DurabilitySettings durabilitySettings,
        CancellationToken cancellationToken);

    public void Initialize(IWolverineRuntime runtime)
    {
        if (_batcher != null) return;

        _batcher = new DatabaseBatcher(this, runtime, runtime.Options.Durability.Cancellation);

        if (Settings.IsMaster && runtime.Options.Transports.NodeControlEndpoint == null && runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            var transport = new DatabaseControlTransport(this, runtime.Options);
            runtime.Options.Transports.Add(transport);

            runtime.Options.Transports.NodeControlEndpoint = transport.ControlEndpoint;
        }
    }

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
}