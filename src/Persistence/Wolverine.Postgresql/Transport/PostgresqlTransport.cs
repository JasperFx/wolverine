using JasperFx;
using JasperFx.Core;
using Npgsql;
using Spectre.Console;
using Weasel.Postgresql;
using Wolverine.Configuration;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlTransport : BrokerTransport<PostgresqlQueue>, ITransportConfiguresRuntime, IAgentFamilySource
{
    public const string ProtocolName = "postgresql";

    public PostgresqlTransport() : base(ProtocolName, "PostgreSQL Transport")
    {
        Queues = new LightweightCache<string, PostgresqlQueue>(name => new PostgresqlQueue(name, this));
    }

    public override Uri ResourceUri => new Uri("postgresql-transport://");

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName { get; set; } = "wolverine_queues";
    
    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName { get; set; } = "public";

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

        if (Store == null)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL transport is configured for usage, but the envelope storage is incompatible: {runtime.Storage}");
        }
    }

    public LightweightCache<string, PostgresqlQueue> Queues { get; }

    protected override IEnumerable<PostgresqlQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLower();
    }

    protected override PostgresqlQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
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
            throw new ArgumentOutOfRangeException(
                "The PostgreSQL transport can only be used if PostgreSQL is the backing message store");
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
        NpgsqlDataSource dataSource = null;
        if (Store is PostgresqlMessageStore store)
        {
            dataSource = store.NpgsqlDataSource;
        }

        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            var raw = (DateTime)await conn.CreateCommand("select (now())::timestamp").ExecuteScalarAsync();
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