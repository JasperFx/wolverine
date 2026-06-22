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
        if (runtime.Storage is SqlServerMessageStore store)
        {
            Storage = store;
        }
        else if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is SqlServerMessageStore s)
        {
            Storage = s;
        }
        else
        {
            throw new InvalidOperationException(
                "The NServiceBus Sql Server interop transport can only be used if Wolverine's message persistence is also Sql Server backed");
        }

        Settings = Storage.Settings;

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
