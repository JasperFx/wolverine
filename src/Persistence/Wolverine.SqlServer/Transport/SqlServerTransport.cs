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

    /// <summary>
    /// Opt into the higher-throughput queue table storage layout: queue and scheduled tables are
    /// clustered on a monotonic <c>seq</c> identity column (for FIFO dequeue and contiguous deletes)
    /// with a unique non-clustered index on the message id, instead of a clustered primary key on a
    /// random Guid. Off by default. Enable via <see cref="SqlServerPersistenceExpression.OptimizeQueueThroughput"/>.
    /// This is the default for every queue in this transport; an individual queue can opt in or out
    /// through <see cref="SqlServerQueue.OptimizeThroughput"/> (sharded topology queues opt in).
    /// </summary>
    public bool OptimizeQueueThroughput { get; set; }

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
