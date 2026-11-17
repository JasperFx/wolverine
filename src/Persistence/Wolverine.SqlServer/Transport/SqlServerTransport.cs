using JasperFx;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Weasel.SqlServer;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.SqlServer.Transport;

public class SqlServerTransport : BrokerTransport<SqlServerQueue>
{
    public const string ProtocolName = "sqlserver";

    public SqlServerTransport(DatabaseSettings settings) : this(settings, settings.SchemaName)
    {
    }

    public SqlServerTransport(DatabaseSettings settings, string? transportSchemaName) : base(ProtocolName,
        "Sql Server Transport", [ProtocolName])
    {
        Queues = new LightweightCache<string, SqlServerQueue>(name => new SqlServerQueue(name, this));
        Settings = settings;
        if (settings.SchemaName.IsNotEmpty())
        {
            TransportSchemaName = settings.SchemaName;
            MessageStorageSchemaName = settings.SchemaName;
        }

        if (transportSchemaName.IsNotEmpty())
        {
            TransportSchemaName = transportSchemaName;
        }
    }

    public override Uri ResourceUri => new Uri("sqlserver-transport://");

    public LightweightCache<string, SqlServerQueue> Queues { get; }

    private string _transportSchemaName = "dbo";

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    /// <remarks>
    /// GH-3884: settable post-construction (mirroring <c>PostgresqlTransport</c>) so the
    /// Wolverine.Polecat integration can stamp an explicitly configured
    /// <c>PolecatIntegration.TransportSchemaName</c> onto the transport at host build time.
    /// The queue/scheduled tables are built lazily per queue, so reassignment before the
    /// runtime initializes the transport is safe.
    /// </remarks>
    public string TransportSchemaName
    {
        get => _transportSchemaName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(TransportSchemaName));
            _transportSchemaName = value;
        }
    }

    private string _messageStorageSchemaName = "dbo";

    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName
    {
        get => _messageStorageSchemaName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(MessageStorageSchemaName));
            _messageStorageSchemaName = value;
        }
    }

    private bool? _optimizeQueueThroughput;
    private bool? _enableDeterministicQueueOrdering;

    /// <summary>
    /// Use the higher-throughput queue table storage layout: queue and scheduled tables are
    /// clustered on a monotonic <c>seq</c> identity column (for FIFO dequeue and contiguous deletes)
    /// with a unique non-clustered index on the message id, instead of a clustered primary key on a
    /// random Guid. This is the default for every queue in this transport; an individual queue can opt in or out
    /// through <see cref="SqlServerQueue.OptimizeThroughput"/> (sharded topology queues opt in).
    /// <para>
    /// GH-5192: on by default from 7.0 for the queue tables Wolverine creates. A queue table that already exists
    /// keeps the layout it has, because moving it to the other layout rebuilds it under an exclusive lock.
    /// Setting this explicitly, through <see cref="SqlServerPersistenceExpression.OptimizeQueueThroughput"/> or
    /// <see cref="SqlServerPersistenceExpression.UseClassicQueueLayout"/>, rebuilds an existing table on the
    /// other layout, as in 6.x. <c>RestoreV6Defaults()</c> makes the classic layout the default again.
    /// </para>
    /// </summary>
    public bool OptimizeQueueThroughput
    {
        get => _optimizeQueueThroughput ?? !QueueV6DefaultsRestored;
        set => _optimizeQueueThroughput = value;
    }

    /// <summary>
    ///     GH-5192. Only an explicit <see cref="OptimizeQueueThroughput" /> rebuilds an existing queue table.
    /// </summary>
    internal bool OptimizeQueueThroughputIsExplicit => _optimizeQueueThroughput.HasValue;

    /// <summary>
    ///     Use persistent identity values to break timestamp ties when dequeueing Sql Server
    ///     queue messages, through a <c>queue_order</c> column and index on the ready and scheduled
    ///     queue tables of every queue on the classic storage layout. Queues using the
    ///     <see cref="OptimizeQueueThroughput" /> layout already have a <c>seq</c> identity column that
    ///     is used as the tie-breaker instead, so their schema is not changed. It applies to queues
    ///     managed by this transport, including tenant databases. Configure it before startup; changing
    ///     it at runtime is not supported.
    ///     <para>
    ///         GH-5192: on by default from 7.0 for the queue tables Wolverine creates. An existing classic
    ///         table without the column keeps its layout and is dequeued in timestamp order. Setting this to
    ///         <c>true</c> explicitly adds the column to existing tables as well, as in 6.x.
    ///     </para>
    /// </summary>
    public bool EnableDeterministicQueueOrdering
    {
        get => _enableDeterministicQueueOrdering ?? !QueueV6DefaultsRestored;
        set => _enableDeterministicQueueOrdering = value;
    }

    /// <summary>
    ///     GH-5192. Only an explicit <see cref="EnableDeterministicQueueOrdering" /> migrates an existing table.
    /// </summary>
    internal bool DeterministicQueueOrderingIsExplicit => _enableDeterministicQueueOrdering.HasValue;

    /// <summary>
    ///     GH-5192. Whether the application restored the Wolverine 6.x defaults for the queue tables. Named apart from
    ///     <c>BrokerTransport.V6DefaultsRestored</c> (GH-5196), which this transport inherits.
    ///     <para>
    ///         GH-5511. Resolved from <see cref="WolverineOptions.V6DefaultsRestored" /> when the transport starts
    ///         (<see cref="ResolveQueueV6Defaults" />), never at registration, because <c>RestoreV6Defaults()</c> may come
    ///         after the transport is configured. Until then it falls back to the flag <c>BrokerTransport</c> reads in
    ///         <c>InitializeAsync()</c>. An explicit setting of either queue-table option still wins over both.
    ///     </para>
    /// </summary>
    internal bool QueueV6DefaultsRestored
    {
        get => _queueV6DefaultsRestored ?? V6DefaultsRestored;
        set => _queueV6DefaultsRestored = value;
    }

    private bool? _queueV6DefaultsRestored;

    /// <summary>
    ///     GH-5511. Read <see cref="WolverineOptions.V6DefaultsRestored" /> before the first queue table is built. The
    ///     tables are built once and cached, so this runs ahead of anything that registers or provisions them.
    /// </summary>
    internal void ResolveQueueV6Defaults(WolverineOptions options)
    {
        _queueV6DefaultsRestored ??= options.V6DefaultsRestored;
    }

    protected override IEnumerable<SqlServerQueue> endpoints()
    {
        return Queues;
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLowerInvariant();
    }

    /// <summary>
    ///     GH-4296. With multi-tenancy by database, the listener registered for a queue is a single
    ///     <see cref="MultiTenantedQueueListener"/> sitting at the bare "sqlserver://queue" address, while the
    ///     per-database listeners underneath it stamp "sqlserver://queue/database" onto every envelope they
    ///     receive. Map the second shape back to the first so that inbox recovery can find the listener an
    ///     orphaned row belongs to. Exclusive queues are excluded on purpose: those listen through the sticky
    ///     per-tenant agents, which DO register the per-database address themselves.
    /// </summary>
    public override Uri? TryResolveListenerAddress(Uri receivedAt)
    {
        if (Databases == null) return null;
        if (receivedAt.Scheme != Protocol) return null;

        var queueName = SanitizeIdentifier(receivedAt.Host);
        if (!Queues.Contains(queueName)) return null;

        var queue = Queues[queueName];
        if (queue.ListenerScope == ListenerScope.Exclusive) return null;

        // Already the address the queue endpoint itself is registered under, so there is nothing to translate
        return queue.Uri == receivedAt ? null : queue.Uri;
    }

    protected override SqlServerQueue findEndpointByUri(Uri uri)
    {
        // The fluent API (ListenToSqlServerQueue / ToSqlServerQueue) runs queue names through
        // MaybeCorrectName, but a queue reached ONLY by Uri (e.g. ListenForMessagesFrom on
        // "sqlserver://my-service-control") used to land here raw — and the queue name is
        // interpolated into the wolverine_queue_* table names, so a dash produced invalid,
        // unbracketed DDL. Correct the name at lookup exactly like the Oracle transport does
        // (GH-3820): SanitizeIdentifier rather than MaybeCorrectName, because a Uri built from
        // an already-corrected name carries any IdentifierPrefix and MaybeCorrectName would
        // prepend it a second time. SanitizeIdentifier is idempotent, so fluent-registered
        // queues resolve to the same endpoint they always did.
        var queueName = SanitizeIdentifier(uri.Host);
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        // GH-5511. Before the first queue table is built: RestoreV6Defaults() gives new queue tables the 6.x layout
        ResolveQueueV6Defaults(runtime.Options);

        AutoProvision = AutoProvision || runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None;

        if (runtime.Storage is SqlServerMessageStore store)
        {
            Storage = store;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is SqlServerMessageStore s)
        {
            Storage = s;
            Databases = mt;
        }
        else
        {
            // #3248 — the Main envelope store is a different engine (e.g. a host that persists to
            // PostgreSQL but wires a SQL Server queue transport). The transport's queue tables only need
            // a SQL Server database, not the Main store, so bind to a same-engine store registered as
            // Ancillary (see MessageStoreRole.Ancillary / role: passthrough) instead of throwing.
            var sqlServerStores = await runtime.Stores.FindAllAsync<SqlServerMessageStore>();
            if (sqlServerStores.Count > 0)
            {
                // A host can carry several same-engine stores (e.g. a primary + ancillary store on the
                // same server); they are co-located in practice, so the first is a safe binding.
                Storage = sqlServerStores[0];
            }
            else
            {
                throw new InvalidOperationException(
                    "The Sql Server Transport requires at least one Sql Server-backed message store (the Main " +
                    "store, or an Ancillary store registered with role: MessageStoreRole.Ancillary), but found none.");
            }
        }

        Settings = Storage.Settings;

        // This is de facto a little environment test
        await using var conn = new SqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();
        await conn.CloseAsync();
    }

    internal DatabaseSettings Settings { get; set; }

    internal SqlServerMessageStore Storage { get; set; } = null!;

    internal MultiTenantedMessageStore? Databases { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);
    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        await using var conn = new SqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand("select SYSDATETIMEOFFSET()");
        return (DateTimeOffset)(await cmd.ExecuteScalarAsync())!;
    }
}
