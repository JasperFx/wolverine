using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : DatabaseBase<T>,
    IMessageDatabase, IMessageInbox, IMessageOutbox, IMessageStoreAdmin where T : DbConnection, new()
{
    protected readonly CancellationToken _cancellation;
    private readonly string _outgoingEnvelopeSql;
    protected readonly DatabaseSettings _settings;
    private DatabaseBatcher? _batcher;
    private string _schemaName;

    protected MessageDatabase(DatabaseSettings databaseSettings, DurabilitySettings settings,
        ILogger logger, Migrator migrator, string defaultSchema) : base(new MigrationLogger(logger),
        AutoCreate.CreateOrUpdate, migrator,
        "WolverineEnvelopeStorage", databaseSettings.ConnectionString!)
    {
        if (databaseSettings.ConnectionString == null)
        {
            throw new ArgumentNullException(nameof(DatabaseSettings.ConnectionString));
        }

        _settings = databaseSettings;
        _schemaName = databaseSettings.SchemaName ?? defaultSchema;

        IncomingFullName = $"{SchemaName}.{DatabaseConstants.IncomingTable}";
        OutgoingFullName = $"{SchemaName}.{DatabaseConstants.OutgoingTable}";

        Durability = settings;
        _cancellation = settings.Cancellation;

        _cancellation = settings.Cancellation;
        _deleteIncomingEnvelopeById =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = @keepUntil where id = @id";
        _incrementIncominEnvelopeAttempts =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set attempts = @attempts where id = @id";

        // ReSharper disable once VirtualMemberCallInConstructor
        _outgoingEnvelopeSql = determineOutgoingEnvelopeSql(settings);

        Nodes = buildNodeStorage(databaseSettings);
    }
    
    

    public bool IsMaster => Settings.IsMaster;

    public string Name { get; set; } = TransportConstants.Default;

    public DatabaseSettings Settings => _settings;

    public string OutgoingFullName { get; private set; }

    public string IncomingFullName { get; private set; }

    public INodeAgentPersistence Nodes { get; }

    public IMessageInbox Inbox => this;

    public IMessageOutbox Outbox => this;

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

    public DurabilitySettings Durability { get; }

    public Task EnqueueAsync(IDatabaseOperation operation)
    {
        if (_batcher == null)
        {
            throw new InvalidOperationException("This message database has not yet been initialized");
        }

        return _batcher.EnqueueAsync(operation);
    }

    public Task InitializeAsync(IWolverineRuntime runtime)
    {
        _batcher = new DatabaseBatcher(this, runtime, runtime.Options.Durability.Cancellation);

        if (Settings.IsMaster && runtime.Options.Transports.NodeControlEndpoint == null)
        {
            var transport = new DatabaseControlTransport(this, runtime.Options);
            runtime.Options.Transports.Add(transport);

            runtime.Options.Transports.NodeControlEndpoint = transport.ControlEndpoint;
        }

        return Task.CompletedTask;
    }

    public IMessageStoreAdmin Admin => this;

    public abstract void Describe(TextWriter writer);

    public Task DrainAsync()
    {
        return _batcher.DrainAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_batcher != null)
        {
            await _batcher.DisposeAsync();
        }
    }

    DbConnection IMessageDatabase.CreateConnection()
    {
        return CreateConnection();
    }

    public DbCommandBuilder ToCommandBuilder()
    {
        return CreateConnection().ToCommandBuilder();
    }

    public async Task ReleaseIncomingAsync(int ownerId)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        await conn
            .CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @owner")
            .With("owner", ownerId)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(_cancellation);

        var impacted = await conn
            .CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = 0 where owner_id = @owner and {DatabaseConstants.ReceivedAt} = @uri")
            .With("owner", ownerId)
            .With("uri", receivedAt.ToString())
            .ExecuteNonQueryAsync(_cancellation);
    }

    protected abstract INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings);

    public DbCommand CreateCommand(string command)
    {
        var cmd = CreateConnection().CreateCommand();
        cmd.CommandText = command;

        return cmd;
    }

    public DbCommand CallFunction(string functionName)
    {
        var cmd = CreateConnection().CreateCommand();
        cmd.CommandText = SchemaName + "." + functionName;

        cmd.CommandType = CommandType.StoredProcedure;

        return cmd;
    }

    public abstract IEnumerable<ISchemaObject> AllObjects();
}