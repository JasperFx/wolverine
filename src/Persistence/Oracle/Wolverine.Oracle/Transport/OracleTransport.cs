using JasperFx;
using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Spectre.Console;
using Weasel.Oracle;
using Wolverine.Configuration;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Oracle.Transport;

public class OracleTransport : BrokerTransport<OracleQueue>
{
    public const string ProtocolName = "oracle";

    public OracleTransport() : base(ProtocolName, "Oracle Transport", [ProtocolName])
    {
        Queues = new LightweightCache<string, OracleQueue>(name => new OracleQueue(name, this));
    }

    public override Uri ResourceUri => new Uri("oracle-transport://");

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName { get; set; } = "WOLVERINE_QUEUES";

    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName { get; set; } = "WOLVERINE";

    public LightweightCache<string, OracleQueue> Queues { get; }

    protected override IEnumerable<OracleQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToUpperInvariant();
    }

    protected override OracleQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        if (runtime.Storage is OracleMessageStore store)
        {
            Store = store;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is OracleMessageStore s)
        {
            Store = s;
            Databases = mt;
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                "The Oracle transport can only be used if Oracle is the backing message store");
        }

        // This is de facto a little environment test
        await runtime.Storage.Admin.CheckConnectivityAsync(CancellationToken.None);

        AutoProvision = AutoProvision || runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None;

        foreach (var queue in Queues)
        {
            Store.AddTable(queue.QueueTable);
            Store.AddTable(queue.ScheduledTable);
        }

        MessageStorageSchemaName = Store.SchemaName;
    }

    internal MultiTenantedMessageStore? Databases { get; set; }

    internal OracleMessageStore? Store { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);
    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        OracleDataSource? dataSource = null;
        if (Store is OracleMessageStore store)
        {
            dataSource = store.OracleDataSource;
        }

        await using var conn = await dataSource!.OpenConnectionAsync();
        try
        {
            var cmd = conn.CreateCommand("SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) FROM DUAL");
            var raw = await cmd.ExecuteScalarAsync();
            return (DateTimeOffset)raw!;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
