using JasperFx.Core;
using Npgsql;
using Spectre.Console;
using Weasel.Postgresql;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlTransport : BrokerTransport<PostgresqlQueue>, ITransportConfiguresRuntime
{
    public const string ProtocolName = "postgresql";

    public PostgresqlTransport() : base(ProtocolName, "PostgreSQL Transport")
    {
        Queues = new LightweightCache<string, PostgresqlQueue>(name => new PostgresqlQueue(name, this));
    }

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string SchemaName { get; set; } = "wolverine_queues";

    public async ValueTask ConfigureAsync(IWolverineRuntime runtime)
    {
        if (runtime.Storage is PostgresqlMessageStore store)
        {
            foreach (var queue in Queues)
            {
                store.AddTable(queue.QueueTable);
                store.AddTable(queue.ScheduledTable);
            }

            Store = store;
        }
        else if (runtime.Storage is MultiTenantedMessageDatabase tenants)
        {
            Store = tenants.Master as PostgresqlMessageStore;
            
            await tenants.ConfigureDatabaseAsync(messageStore =>
            {
                if (messageStore is PostgresqlMessageStore s)
                {
                    foreach (var queue in Queues)
                    {
                        s.AddTable(queue.QueueTable);
                        s.AddTable(queue.ScheduledTable);
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL backed transport can only be used with PostgreSQL is the active envelope storage mechanism");
                }

                return new ValueTask();
            });
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
        // This is de facto a little environment test
        await runtime.Storage.Admin.CheckConnectivityAsync(CancellationToken.None);
    }

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
            dataSource = store.DataSource;
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
}