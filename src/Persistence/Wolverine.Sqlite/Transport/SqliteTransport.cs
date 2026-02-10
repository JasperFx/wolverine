using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Sqlite.Transport;

public class SqliteTransport : BrokerTransport<SqliteQueue>, ITransportConfiguresRuntime
{
    public const string ProtocolName = "sqlite";

    public SqliteTransport() : base(ProtocolName, "SQLite Transport", ["sqlite"])
    {
        Queues = new LightweightCache<string, SqliteQueue>(name => new SqliteQueue(name, this));
    }

    public override Uri ResourceUri => new Uri("sqlite-transport://");

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName { get; set; } = "main";

    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName { get; set; } = "main";

    public async ValueTask ConfigureAsync(IWolverineRuntime runtime)
    {
        // This is important, let the SQLite queues get built automatically
        AutoProvision = AutoProvision || runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None;
        var stores = await runtime.Stores.FindAllAsync<SqliteMessageStore>();
        if (!stores.Any())
        {
            throw new InvalidOperationException(
                $"The SQLite transport is configured for usage, but the envelope storage is incompatible: {runtime.Storage}");
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

        if (runtime.Storage is SqliteMessageStore s)
        {
            Store = s;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt)
        {
            if (mt.Main is SqliteMessageStore p)
            {
                Store = p;
                Databases = mt;
            }
        }

        if (Store == null)
        {
            throw new InvalidOperationException(
                $"The SQLite transport is configured for usage, but the envelope storage is incompatible: {runtime.Storage}");
        }
    }

    public LightweightCache<string, SqliteQueue> Queues { get; }

    protected override IEnumerable<SqliteQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLower();
    }

    protected override SqliteQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        if (runtime.Storage is SqliteMessageStore store)
        {
            Store = store;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is SqliteMessageStore s)
        {
            Store = s;
            Databases = mt;
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                "The SQLite transport can only be used if SQLite is the backing message store");
        }

        // This is de facto a little environment test
        await runtime.Storage.Admin.CheckConnectivityAsync(CancellationToken.None);
    }

    internal MultiTenantedMessageStore? Databases { get; set; }

    internal SqliteMessageStore? Store { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);
    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        if (Store?.DataSource == null)
        {
            throw new InvalidOperationException("SQLite store is not initialized");
        }

        await using var conn = await Store.DataSource.OpenConnectionAsync().ConfigureAwait(false);

        var raw = (string)await conn.CreateCommand("select datetime('now')").ExecuteScalarAsync();
        return new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(raw), DateTimeKind.Utc));
    }
}
