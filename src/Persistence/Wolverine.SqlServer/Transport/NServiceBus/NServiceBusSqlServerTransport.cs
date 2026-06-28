using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.SqlServer.Transport.NServiceBus;

/// <summary>
/// A Wolverine transport that reads and writes the SQL Server queue tables owned by an
/// NServiceBus endpoint, enabling bidirectional interop with NServiceBus over its
/// documented SQL Server transport contract. Requires Wolverine itself to be using
/// SQL Server backed message persistence (for the durable inbox/outbox); only the foreign
/// queue tables belong to NServiceBus.
/// </summary>
public class NServiceBusSqlServerTransport : BrokerTransport<NServiceBusSqlServerQueue>
{
    public const string ProtocolName = "nservicebus-sqlserver";

    public NServiceBusSqlServerTransport() : base(ProtocolName, "NServiceBus Sql Server Interop", [ProtocolName])
    {
        Queues = new LightweightCache<string, NServiceBusSqlServerQueue>(name => new NServiceBusSqlServerQueue(name, this));

        // NServiceBus owns its own tables; do not provision them by default.
        AutoProvision = false;
    }

    public LightweightCache<string, NServiceBusSqlServerQueue> Queues { get; }

    /// <summary>
    /// Schema name for the NServiceBus queue tables. Defaults to "dbo", matching the
    /// NServiceBus SQL Server transport default.
    /// </summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>
    /// Optional explicit connection string for the single database that owns the NServiceBus interop queue tables.
    /// When set, the transport binds to this one database and is fully decoupled from Wolverine's message storage —
    /// use this when storage is multi-tenanted (a database per tenant) but the NServiceBus queues live on one shared
    /// database that must NOT be replicated per tenant. When null (the default), the transport falls back to
    /// Wolverine's SQL Server message store (the <c>Main</c> store under multi-tenancy).
    /// </summary>
    public string? ConnectionString { get; set; }

    public override Uri ResourceUri => new("nservicebus-sqlserver-transport://");

    protected override IEnumerable<NServiceBusSqlServerQueue> endpoints()
    {
        return Queues;
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        return Queues;
    }

    // NServiceBus queue/table names are matched verbatim against the foreign endpoint name.
    public override string SanitizeIdentifier(string identifier)
    {
        return identifier;
    }

    protected override NServiceBusSqlServerQueue findEndpointByUri(Uri uri)
    {
        return Queues[uri.Host];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        if (ConnectionString.IsNotEmpty())
        {
            // Explicit single database for the NServiceBus interop queues, decoupled from Wolverine's
            // (possibly multi-tenanted) message storage. No requirement that storage itself be SQL Server backed:
            // the interop queues are buffered and never touch Wolverine's durable inbox/outbox tables.
            Settings = new DatabaseSettings { ConnectionString = ConnectionString, SchemaName = SchemaName };
        }
        else if (runtime.Storage is SqlServerMessageStore store)
        {
            Storage = store;
            Settings = Storage.Settings;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is SqlServerMessageStore s)
        {
            Storage = s;
            Settings = Storage.Settings;
        }
        else
        {
            throw new InvalidOperationException(
                "The NServiceBus Sql Server interop transport requires either an explicit connection string " +
                "(UseNServiceBusSqlServerInterop(connectionString: ...)) or Wolverine's message persistence to be Sql Server backed");
        }

        // de facto environment test
        await using var conn = new SqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();
        await conn.CloseAsync();
    }

    internal DatabaseSettings Settings { get; set; } = null!;

    internal SqlServerMessageStore Storage { get; set; } = null!;

    /// <summary>
    /// Resolve the SQL Server connection string used for the NServiceBus queue tables. Works
    /// before <see cref="ConnectAsync"/> has run (e.g. from the Weasel command line) by falling
    /// back to the SQL Server message store registered for Wolverine's own persistence.
    /// </summary>
    internal string ResolveConnectionString(IWolverineRuntime runtime)
    {
        // An explicit connection string pins the interop queues to one database regardless of storage tenancy,
        // and is resolvable before ConnectAsync has run (e.g. from the Weasel command line).
        if (ConnectionString.IsNotEmpty())
        {
            return ConnectionString!;
        }

        if (Settings is not null)
        {
            return Settings.ConnectionString!;
        }

        if (runtime.Storage is SqlServerMessageStore store)
        {
            return store.Settings.ConnectionString!;
        }

        if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is SqlServerMessageStore s)
        {
            return s.Settings.ConnectionString!;
        }

        throw new InvalidOperationException(
            "The NServiceBus Sql Server interop transport requires Sql Server backed message persistence");
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("NServiceBus Queue");
        yield return new PropertyColumn("Count", Justify.Right);
    }
}
