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

    /// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName { get; private set; } = "dbo";

    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName { get; private set; } = "dbo";

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

    protected override SqlServerQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
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
