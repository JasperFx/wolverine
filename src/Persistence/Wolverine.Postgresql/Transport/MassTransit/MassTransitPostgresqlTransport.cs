using JasperFx.Core;
using Npgsql;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport.MassTransit;

/// <summary>
/// A Wolverine transport that interoperates with MassTransit's PostgreSQL SQL transport by
/// calling MassTransit's own stored functions (<c>transport.send_message</c>,
/// <c>transport.fetch_messages</c>, etc.) rather than writing the queue tables directly.
/// MassTransit owns, provisions, and migrates its <c>transport</c> schema; Wolverine only
/// invokes the documented functions. Requires Wolverine itself to be using PostgreSQL backed
/// message persistence (for the durable inbox/outbox).
/// </summary>
public class MassTransitPostgresqlTransport : BrokerTransport<MassTransitPostgresqlQueue>
{
    public const string ProtocolName = "masstransit-postgresql";

    private string _massTransitHost = "localhost:5432";

    public MassTransitPostgresqlTransport() : base(ProtocolName, "MassTransit PostgreSQL Interop", [ProtocolName])
    {
        Queues = new LightweightCache<string, MassTransitPostgresqlQueue>(name => new MassTransitPostgresqlQueue(name, this));

        // MassTransit owns its own schema; do not provision it by default.
        AutoProvision = false;
    }

    public LightweightCache<string, MassTransitPostgresqlQueue> Queues { get; }

    /// <summary>
    /// Schema name for the MassTransit transport functions and tables. Defaults to "transport",
    /// matching the MassTransit PostgreSQL transport default.
    /// </summary>
    public string SchemaName { get; set; } = "transport";

    public override Uri ResourceUri => new("masstransit-postgresql-transport://");

    /// <summary>
    /// The MassTransit "host" identity ("{host}:{port}") used to build the MassTransit
    /// <c>db://</c> addresses for source/destination/response URIs. Derived from the Npgsql
    /// connection string during <see cref="ConnectAsync"/>.
    /// </summary>
    internal string MassTransitHost => _massTransitHost;

    protected override IEnumerable<MassTransitPostgresqlQueue> endpoints()
    {
        return Queues;
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        return Queues;
    }

    // MassTransit queue names are matched verbatim against the foreign endpoint name.
    public override string SanitizeIdentifier(string identifier)
    {
        return identifier;
    }

    protected override MassTransitPostgresqlQueue findEndpointByUri(Uri uri)
    {
        return Queues[uri.Host];
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
        }
        else
        {
            throw new InvalidOperationException(
                "The MassTransit PostgreSQL interop transport can only be used if Wolverine's message persistence is also PostgreSQL backed");
        }

        _massTransitHost = ResolveMassTransitHost(Store.Settings.ConnectionString!);

        // de facto environment test
        await using var conn = await Store.NpgsqlDataSource.OpenConnectionAsync();
        await conn.CloseAsync();
    }

    internal PostgresqlMessageStore Store { get; set; } = null!;

    /// <summary>
    /// Resolve the PostgreSQL connection string used for the MassTransit transport. Works
    /// before <see cref="ConnectAsync"/> has run by falling back to the PostgreSQL message
    /// store registered for Wolverine's own persistence.
    /// </summary>
    internal string ResolveConnectionString(IWolverineRuntime runtime)
    {
        if (Store is not null)
        {
            return Store.Settings.ConnectionString!;
        }

        if (runtime.Storage is PostgresqlMessageStore store)
        {
            return store.Settings.ConnectionString!;
        }

        if (runtime.Storage is MultiTenantedMessageStore mt && mt.Main is PostgresqlMessageStore s)
        {
            return s.Settings.ConnectionString!;
        }

        throw new InvalidOperationException(
            "The MassTransit PostgreSQL interop transport requires PostgreSQL backed message persistence");
    }

    /// <summary>
    /// Build the MassTransit "host:port" identity from the Npgsql connection string. MassTransit
    /// uses this to build its <c>db://{host}:{port}/{queue}</c> source/destination addresses.
    /// </summary>
    internal static string ResolveMassTransitHost(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = builder.Host.IsNotEmpty() ? builder.Host! : "localhost";
        var port = builder.Port == 0 ? 5432 : builder.Port;
        return $"{host}:{port}";
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("MassTransit Queue");
        yield return new PropertyColumn("Count", Justify.Right);
    }
}
