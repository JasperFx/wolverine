using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Sqlite.Transport;

public class SqliteTransport : BrokerTransport<SqliteQueue>, ITransportConfiguresRuntime
{
    public const string ProtocolName = "sqlite";

    private string? _prefixName;

    public SqliteTransport() : base(ProtocolName, "SQLite Transport", ["sqlite"])
    {
        Queues = new LightweightCache<string, SqliteQueue>(name => new SqliteQueue(name, this));
    }

    public override Uri ResourceUri => new Uri("sqlite-transport://");

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

    /// <summary>
    ///     Uses SQLite's implicit rowid to break timestamp ties when dequeueing SQLite queue
    ///     messages, and promotes scheduled messages in execution time order with the same
    ///     tie-breaker. No column is added, but this adds an index to each of the ready and
    ///     scheduled queue tables. It applies to queues managed by this transport, including
    ///     tenant databases, and is opt-in to avoid changing existing schemas automatically.
    ///     Configure it before startup; changing it at runtime is not supported.
    /// </summary>
    public bool EnableDeterministicQueueOrdering { get; set; }

    /// <summary>
    ///     GH-3947: an optional prefix for the native SQLite queue and scheduled-message tables.
    ///     SQLite has no schemas, so <c>"reporting"</c> names the tables for an <c>orders</c> queue
    ///     <c>reporting_wolverine_queue_orders</c> and <c>reporting_wolverine_queue_orders_scheduled</c>,
    ///     which lets separate sets of Wolverine queue tables share one database file. The default is
    ///     no prefix, which keeps the historical table names, and SQLite's built-in schema name
    ///     <c>main</c> also means no prefix. Configure this before startup; changing it later points
    ///     the queues at new, empty tables, and nothing copies messages across.
    /// </summary>
    public string? PrefixName
    {
        get => _prefixName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(PrefixName));
            _prefixName = value?.ToLowerInvariant();
        }
    }

    internal string QueueTableNameFor(string tableName)
    {
        return TablePrefixing.Apply(PrefixName, tableName);
    }

    protected override IEnumerable<SqliteQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLowerInvariant();
    }

    protected override SqliteQueue findEndpointByUri(Uri uri)
    {
        // Same correction as the SQL Server / Postgresql / Oracle (GH-3820) transports: a queue
        // reached only by Uri bypasses the fluent API's MaybeCorrectName, and a dash in the raw
        // name reaches the wolverine_queue_* table DDL. SanitizeIdentifier (idempotent), not
        // MaybeCorrectName (would double-apply an IdentifierPrefix).
        var queueName = SanitizeIdentifier(uri.Host);
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

        await using var timeCmd = conn.CreateCommand("select datetime('now')");
        var raw = (string?)await timeCmd.ExecuteScalarAsync();
        return new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(raw!), DateTimeKind.Utc));
    }
}
