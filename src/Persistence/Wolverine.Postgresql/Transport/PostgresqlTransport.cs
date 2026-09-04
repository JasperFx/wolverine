using JasperFx;
using JasperFx.Core;
using Npgsql;
using Spectre.Console;
using Weasel.Postgresql;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlTransport : BrokerTransport<PostgresqlQueue>, ITransportConfiguresRuntime, IAgentFamilySource
{
    public const string ProtocolName = "postgresql";

    public PostgresqlTransport() : base(ProtocolName, "PostgreSQL Transport", [ProtocolName])
    {
        Queues = new LightweightCache<string, PostgresqlQueue>(name => new PostgresqlQueue(name, this));
    }

    public override Uri ResourceUri => new Uri("postgresql-transport://");

    private string _transportSchemaName = "wolverine_queues";

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName
    {
        get => _transportSchemaName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(TransportSchemaName));
            _transportSchemaName = value;
        }
    }
    
    private string _messageStorageSchemaName = "public";

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

    public async ValueTask ConfigureAsync(IWolverineRuntime runtime)
    {
        // This is important, let the Postgres queues get built automatically
        AutoProvision = AutoProvision || runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None;
        var stores = await runtime.Stores.FindAllAsync<PostgresqlMessageStore>();
        if (!stores.Any())
        {
            throw new InvalidOperationException(
                $"The PostgreSQL transport is configured for usage, but the envelope storage is incompatible: {runtime.Storage}");
        }
        
        foreach (var store in stores)
        {
            foreach (var queue in Queues)
            {
                store.AddTable(queue.QueueTable);
                store.AddTable(queue.ScheduledTable);
            }

            MessageStorageSchemaName = store.SchemaName;
        }

        if (runtime.Storage is PostgresqlMessageStore s)
        {
            Store = s;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt)
        {
            if (mt.Main is PostgresqlMessageStore p)
            {
                Store = p;
                Databases = mt;
            }
        }

        // #3248 — the Main envelope store is a different engine (e.g. a host that persists to SQL Server
        // but wires a PostgreSQL queue transport). Bind to a same-engine store registered as Ancillary
        // instead of throwing — the transport's queue tables only need a PostgreSQL database, not the Main
        // store, and the loop above already provisioned them in every same-engine store. A host can carry
        // several same-engine stores (e.g. a primary + ancillary document store on the same server); they
        // are co-located in practice, so the first is a safe binding. Same fallback as ConnectAsync.
        if (Store == null && stores.Count > 0)
        {
            Store = stores[0];
        }

        if (Store == null)
        {
            throw new InvalidOperationException(
                "The PostgreSQL transport requires at least one PostgreSQL-backed message store (the Main store, " +
                "or an Ancillary store registered with role: MessageStoreRole.Ancillary), but the envelope " +
                $"storage is incompatible: {runtime.Storage}");
        }
    }

    public LightweightCache<string, PostgresqlQueue> Queues { get; }

    protected override IEnumerable<PostgresqlQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLowerInvariant();
    }

    /// <summary>
    ///     GH-4296. With multi-tenancy by database, the listener registered for a queue is a single
    ///     <see cref="MultiTenantedQueueListener"/> sitting at the bare "postgresql://queue" address, while the
    ///     per-database listeners underneath it stamp "postgresql://queue/database" onto every envelope they
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

    protected override PostgresqlQueue findEndpointByUri(Uri uri)
    {
        // A queue reached ONLY by Uri (e.g. ListenForMessagesFrom on "postgresql://my-queue-name")
        // bypasses the fluent API's MaybeCorrectName, and the raw name is interpolated into the
        // wolverine_queue_* table names — a dash produced invalid, unquoted DDL. Correct the name
        // at lookup like the Oracle transport does (GH-3820): SanitizeIdentifier, NOT
        // MaybeCorrectName, which would prepend an IdentifierPrefix a second time on a Uri built
        // from an already-corrected name. SanitizeIdentifier is idempotent for fluent-registered
        // queues.
        var queueName = SanitizeIdentifier(uri.Host);
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        if (runtime.Storage is PostgresqlMessageStore store)
        {
            Store = store;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is PostgresqlMessageStore s)
        {
            Store = s;
            Databases = mt;
        }
        else
        {
            // #3248 — the Main envelope store is a different engine (e.g. a host that persists to SQL
            // Server but wires a PostgreSQL queue transport). The transport's queue tables only need a
            // PostgreSQL database, not the Main store, so bind to a same-engine store registered as
            // Ancillary (see MessageStoreRole.Ancillary / role: passthrough) instead of throwing.
            var postgresStores = await runtime.Stores.FindAllAsync<PostgresqlMessageStore>();
            if (postgresStores.Count > 0)
            {
                Store = postgresStores[0];
            }
            else
            {
                throw new ArgumentOutOfRangeException(
                    "The PostgreSQL transport requires at least one PostgreSQL-backed message store (the Main " +
                    "store, or an Ancillary store registered with role: MessageStoreRole.Ancillary), but found none.");
            }
        }

        // This is de facto a little environment test
        await runtime.Storage.Admin.CheckConnectivityAsync(CancellationToken.None);
    }

    internal MultiTenantedMessageStore? Databases { get; set; }

    internal PostgresqlMessageStore? Store { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);

    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        NpgsqlDataSource? dataSource = null;
        if (Store is PostgresqlMessageStore store)
        {
            dataSource = store.NpgsqlDataSource;
        }

        await using var conn = await dataSource!.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand("select (now())::timestamp");
            var raw = (DateTime)(await cmd.ExecuteScalarAsync())!;
            return new DateTimeOffset(raw, 0.Hours());
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    IEnumerable<IAgentFamily> IAgentFamilySource.BuildAgentFamilySources(IWolverineRuntime runtime)
    {
        if (runtime.Storage is not MultiTenantedMessageStore)
        {
            yield break;
        }
        
        if (Queues.Any(x => x is { IsListener: true, ListenerScope: ListenerScope.Exclusive }))
        {
            yield return new StickyPostgresqlQueueListenerAgentFamily(runtime);
        }
    }
}