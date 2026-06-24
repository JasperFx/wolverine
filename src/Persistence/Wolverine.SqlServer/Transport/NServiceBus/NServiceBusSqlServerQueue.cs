using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport.NServiceBus;

public class NServiceBusSqlServerQueue : Endpoint, IBrokerQueue
{
    internal static Uri ToUri(string name)
    {
        return new Uri($"{NServiceBusSqlServerTransport.ProtocolName}://{name}");
    }

    private NServiceBusSqlServerEnvelopeMapper? _mapper;
    private NServiceBusSqlServerQueueSender? _sender;
    private readonly Lazy<NServiceBusQueueTable> _queueTable;

    public NServiceBusSqlServerQueue(string name, NServiceBusSqlServerTransport parent,
        EndpointRole role = EndpointRole.Application) : base(ToUri(name), role)
    {
        Parent = parent;
        Name = name;
        EndpointName = name;
        BrokerRole = "queue";

        // Interop endpoints exchange the foreign wire format; they cannot participate in
        // Wolverine's durable inbox/outbox table layout, so run buffered like the broker
        // interop transports do.
        Mode = EndpointMode.BufferedInMemory;

        // Lazy so the transport schema name is settled before the table identifier is built.
        _queueTable = new Lazy<NServiceBusQueueTable>(
            () => new NServiceBusQueueTable(new DbObjectName(Parent.SchemaName, Name)));
    }

    public string Name { get; }

    internal NServiceBusSqlServerTransport Parent { get; }

    /// <summary>
    /// The bare queue/table name that NServiceBus should reply to. Set by
    /// <c>UseNServiceBusInterop(replyQueueName)</c>; when null the configured Wolverine
    /// reply endpoint name is used.
    /// </summary>
    public string? InteropReplyQueueName { get; set; }

    /// <summary>
    /// When set, Wolverine maps its <see cref="Envelope.TenantId"/> to and from this NServiceBus
    /// message header on this queue. NServiceBus multi-tenancy is a persistence concern: the tenant
    /// id rides as a user-defined header (the Particular SQL-persistence sample uses
    /// <c>tenant_id</c>) and the receiving endpoint's <c>MultiTenantConnectionBuilder</c> uses it to
    /// open the tenant's database. Null (the default) disables tenant mapping.
    /// </summary>
    public string? TenantHeader { get; set; }

    /// <summary>
    ///     The maximum number of messages to receive in a single poll. Default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    /// <summary>
    ///     How often to poll for new messages when the queue is idle. If null, falls back to
    ///     DurabilitySettings.ScheduledJobPollingTime (default 5s).
    /// </summary>
    public TimeSpan? PollingInterval { get; set; }

    /// <summary>
    /// The Weasel model of the NServiceBus queue table. Used for all schema management and as
    /// the single source of truth for the table name in the send/receive DML.
    /// </summary>
    internal Table QueueTable => _queueTable.Value;

    internal DbObjectName TableIdentifier => QueueTable.Identifier;

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.BufferedInMemory || mode == EndpointMode.Inline;
    }

    internal NServiceBusSqlServerEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        if (_mapper != null) return _mapper;

        DefaultSerializer ??= runtime.Options.DefaultSerializer;

        var replyName = new Lazy<string?>(() =>
        {
            if (InteropReplyQueueName.IsNotEmpty()) return InteropReplyQueueName;
            return Parent.ReplyEndpoint()?.EndpointName;
        });

        _mapper = new NServiceBusSqlServerEnvelopeMapper(this, () => replyName.Value, TenantHeader);
        return _mapper;
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(runtime.LoggerFactory.CreateLogger<NServiceBusSqlServerQueue>());
        }

        var listener = new NServiceBusSqlServerQueueListener(this, runtime, receiver);
        await listener.StartAsync();
        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return _sender ??= new NServiceBusSqlServerQueueSender(this, runtime);
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }
    }

    // IBrokerQueue.CheckAsync compares the live table against the Weasel model. Used by the
    // resource model / db-assert; not on the message hot path.
    public async ValueTask<bool> CheckAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            var delta = await QueueTable.FindDeltaAsync(conn);
            return !delta.HasChanges();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await QueueTable.ApplyChangesAsync(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await QueueTable.Drop(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            if (!await tableExistsAsync(conn)) return;

            await using var cmd = conn.CreateCommand($"DELETE FROM {TableIdentifier}");
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var count = await CountAsync();
        return new Dictionary<string, string> { { "NServiceBus Queue", Name }, { "Count", count.ToString() } };
    }

    public async Task<long> CountAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            if (!await tableExistsAsync(conn)) return 0;

            await using var cmd = conn.CreateCommand($"SELECT COUNT(*) FROM {TableIdentifier}");
            return (int)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Lightweight existence probe (a read, not a schema change) used to guard runtime DML and
    /// the sender ping. Schema creation/migration goes through <see cref="SetupAsync"/> / Weasel.
    /// </summary>
    internal async Task<bool> ExistsAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            return await tableExistsAsync(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task<bool> tableExistsAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END")
            .With("name", TableIdentifier.QualifiedName);
        return (int)(await cmd.ExecuteScalarAsync())! == 1;
    }
}
