using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport.MassTransit;

public class MassTransitPostgresqlQueue : Endpoint, IBrokerQueue, IMassTransitInteropEndpoint
{
    internal static Uri ToUri(string name)
    {
        var uriString = $"{MassTransitPostgresqlTransport.ProtocolName}://{name}";
        return Uri.TryCreate(uriString, UriKind.Absolute, out var uri)
            ? uri
            : new Uri($"{MassTransitPostgresqlTransport.ProtocolName}://queue/{Uri.EscapeDataString(name)}");
    }

    private MassTransitPostgresqlEnvelopeMapper? _mapper;
    private MassTransitPostgresqlQueueSender? _sender;

    public MassTransitPostgresqlQueue(string name, MassTransitPostgresqlTransport parent,
        EndpointRole role = EndpointRole.Application) : base(ToUri(name), role)
    {
        Parent = parent;
        Name = name;
        EndpointName = name;
        BrokerRole = "queue";

        // Interop endpoints exchange the foreign wire format; run buffered like the broker
        // interop transports do.
        Mode = EndpointMode.BufferedInMemory;
    }

    public string Name { get; }

    internal MassTransitPostgresqlTransport Parent { get; }

    /// <summary>
    /// The bare MassTransit queue name that foreign endpoints should reply to. When null the
    /// configured Wolverine reply endpoint name is used.
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

    /// <summary>
    ///     How long a fetched message is leased before MassTransit considers it abandoned and
    ///     re-delivers it. Default is 5 minutes.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = 5.Minutes();

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.BufferedInMemory || mode == EndpointMode.Inline;
    }

    internal MassTransitPostgresqlEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        if (_mapper != null) return _mapper;

        DefaultSerializer ??= runtime.Options.DefaultSerializer;

        var replyName = new Lazy<string?>(() =>
        {
            if (InteropReplyQueueName.IsNotEmpty()) return InteropReplyQueueName;
            return Parent.ReplyEndpoint()?.EndpointName;
        });

        _mapper = new MassTransitPostgresqlEnvelopeMapper(this, () => replyName.Value);
        return _mapper;
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(runtime.LoggerFactory.CreateLogger<MassTransitPostgresqlQueue>());
        }

        var listener = new MassTransitPostgresqlQueueListener(this, runtime, receiver);
        await listener.StartAsync();
        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return _sender ??= new MassTransitPostgresqlQueueSender(this, runtime);
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

    // IBrokerQueue.CheckAsync -- does the MassTransit queue exist (type=1 is a regular queue).
    public async ValueTask<bool> CheckAsync()
    {
        await using var conn = await Parent.Store.NpgsqlDataSource.OpenConnectionAsync();
        try
        {
            return await queueExistsAsync(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await using var conn = await Parent.Store.NpgsqlDataSource.OpenConnectionAsync();
        try
        {
            await using var cmd = conn
                .CreateCommand($"SELECT {Parent.SchemaName}.create_queue_v2(:name)")
                .With("name", Name);
            await cmd.ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        // MassTransit owns the schema and its queue lifecycle; the minimal teardown is a purge.
        await PurgeAsync(logger);
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        await using var conn = await Parent.Store.NpgsqlDataSource.OpenConnectionAsync();
        try
        {
            if (!await queueExistsAsync(conn)) return;

            await using var cmd = conn
                .CreateCommand($"SELECT {Parent.SchemaName}.purge_queue(:name)")
                .With("name", Name);
            await cmd.ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var count = await CountAsync();
        return new Dictionary<string, string> { { "MassTransit Queue", Name }, { "Count", count.ToString() } };
    }

    public async Task<long> CountAsync()
    {
        await using var conn = await Parent.Store.NpgsqlDataSource.OpenConnectionAsync();
        try
        {
            if (!await queueExistsAsync(conn)) return 0;

            await using var cmd = conn.CreateCommand(
                    $"SELECT count(*) FROM {Parent.SchemaName}.message_delivery md JOIN {Parent.SchemaName}.queue q ON q.id = md.queue_id WHERE q.name = :name AND q.type = 1")
                .With("name", Name);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Lightweight existence probe used to guard runtime DML and the sender ping.
    /// </summary>
    internal async Task<bool> ExistsAsync()
    {
        await using var conn = await Parent.Store.NpgsqlDataSource.OpenConnectionAsync();
        try
        {
            return await queueExistsAsync(conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task<bool> queueExistsAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn
            .CreateCommand($"SELECT EXISTS(SELECT 1 FROM {Parent.SchemaName}.queue WHERE name = :name AND type = 1)")
            .With("name", Name);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public Uri? MassTransitUri()
    {
        return $"db://{Parent.MassTransitHost}/{Name}".ToUri();
    }

    public Uri? MassTransitReplyUri()
    {
        if (InteropReplyQueueName.IsNotEmpty())
        {
            return $"db://{Parent.MassTransitHost}/{InteropReplyQueueName}".ToUri();
        }

        if (Parent.ReplyEndpoint() is MassTransitPostgresqlQueue replyQueue)
        {
            return replyQueue.MassTransitUri();
        }

        return null;
    }

    public Uri? TranslateMassTransitToWolverineUri(Uri uri)
    {
        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        return lastSegment.IsNotEmpty() ? ToUri(lastSegment!) : null;
    }
}
