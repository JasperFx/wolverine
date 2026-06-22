using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.SqlServer;
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
    ///     The maximum number of messages to receive in a single poll. Default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    /// <summary>
    ///     How often to poll for new messages when the queue is idle. If null, falls back to
    ///     DurabilitySettings.ScheduledJobPollingTime (default 5s).
    /// </summary>
    public TimeSpan? PollingInterval { get; set; }

    internal string TableIdentifier => $"[{Parent.SchemaName}].[{Name}]";

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

        _mapper = new NServiceBusSqlServerEnvelopeMapper(this, () => replyName.Value);
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

    public async ValueTask<bool> CheckAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand(
                $"SELECT CASE WHEN OBJECT_ID(N'{Parent.SchemaName}.{Name}', N'U') IS NULL THEN 0 ELSE 1 END");
            var exists = (int)(await cmd.ExecuteScalarAsync())!;
            return exists == 1;
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
            await using var schemaCmd = conn.CreateCommand(
                $"IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{Parent.SchemaName}') EXEC('CREATE SCHEMA [{Parent.SchemaName}]')");
            await schemaCmd.ExecuteNonQueryAsync();

            await using var cmd = conn.CreateCommand(CreateTableSql());
            await cmd.ExecuteNonQueryAsync();
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
            await using var cmd = conn.CreateCommand($"DROP TABLE IF EXISTS {TableIdentifier}");
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        if (!await CheckAsync()) return;

        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
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
        if (!await CheckAsync()) return 0;

        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand($"SELECT COUNT(*) FROM {TableIdentifier}");
            return (int)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    // Matches the NServiceBus SQL Server transport queue table layout, including the
    // legacy CorrelationId/ReplyToAddress/Recoverable columns the transport still probes.
    private string CreateTableSql()
    {
        return $@"
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'{Parent.SchemaName}.{Name}') AND type = 'U')
BEGIN
    CREATE TABLE {TableIdentifier} (
        Id uniqueidentifier NOT NULL,
        CorrelationId varchar(255),
        ReplyToAddress varchar(255),
        Recoverable bit NOT NULL,
        Expires datetime,
        Headers nvarchar(max) NOT NULL,
        Body varbinary(max),
        RowVersion bigint IDENTITY(1,1) NOT NULL
    );
    CREATE NONCLUSTERED INDEX [Index_RowVersion] ON {TableIdentifier} (RowVersion);
    CREATE CLUSTERED INDEX [Index_Expires] ON {TableIdentifier} (Expires) WHERE Expires IS NOT NULL;
END";
    }
}
