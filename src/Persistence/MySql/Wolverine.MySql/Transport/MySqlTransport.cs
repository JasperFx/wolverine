using JasperFx;
using JasperFx.Core;
using MySqlConnector;
using Spectre.Console;
using Weasel.MySql;
using Wolverine.Configuration;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.MySql.Transport;

public class MySqlTransport : BrokerTransport<MySqlQueue>
{
    public const string ProtocolName = "mysql";

    public MySqlTransport() : base(ProtocolName, "MySQL Transport", [ProtocolName])
    {
        Queues = new LightweightCache<string, MySqlQueue>(name => new MySqlQueue(name, this));
    }

    public override Uri ResourceUri => new Uri("mysql-transport://");

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName { get; set; } = "wolverine_queues";

    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName { get; set; } = "wolverine";

    public LightweightCache<string, MySqlQueue> Queues { get; }

    protected override IEnumerable<MySqlQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLower();
    }

    protected override MySqlQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        if (runtime.Storage is MySqlMessageStore store)
        {
            Store = store;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is MySqlMessageStore s)
        {
            Store = s;
            Databases = mt;
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                "The MySQL transport can only be used if MySQL is the backing message store");
        }

        // This is de facto a little environment test
        await runtime.Storage.Admin.CheckConnectivityAsync(CancellationToken.None);

        // Configure queues with the store
        AutoProvision = AutoProvision || runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None;

        foreach (var queue in Queues)
        {
            Store.AddTable(queue.QueueTable);
            Store.AddTable(queue.ScheduledTable);
        }

        MessageStorageSchemaName = Store.SchemaName;
    }

    internal MultiTenantedMessageStore? Databases { get; set; }

    internal MySqlMessageStore? Store { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);
    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        MySqlDataSource? dataSource = null;
        if (Store is MySqlMessageStore store)
        {
            dataSource = store.MySqlDataSource;
        }

        await using var conn = await dataSource!.OpenConnectionAsync();
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT UTC_TIMESTAMP(6)";
            var raw = (DateTime)(await cmd.ExecuteScalarAsync())!;
            return new DateTimeOffset(raw, TimeSpan.Zero);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
