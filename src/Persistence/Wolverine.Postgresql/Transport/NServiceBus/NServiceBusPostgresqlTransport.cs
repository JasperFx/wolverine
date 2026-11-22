using JasperFx.Core;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport.NServiceBus;

/// <summary>
/// A Wolverine transport that reads and writes the PostgreSQL queue tables owned by an
/// NServiceBus endpoint, enabling bidirectional interop with NServiceBus over its
/// documented PostgreSQL transport contract. Requires Wolverine itself to be using
/// PostgreSQL backed message persistence (for the durable inbox/outbox); only the foreign
/// queue tables belong to NServiceBus.
/// </summary>
public class NServiceBusPostgresqlTransport : BrokerTransport<NServiceBusPostgresqlQueue>
{
    public const string ProtocolName = "nservicebus-postgresql";

    public NServiceBusPostgresqlTransport() : base(ProtocolName, "NServiceBus PostgreSQL Interop", [ProtocolName])
    {
        Queues = new LightweightCache<string, NServiceBusPostgresqlQueue>(name => new NServiceBusPostgresqlQueue(name, this));

        // NServiceBus owns its own tables; do not provision them by default.
        AutoProvision = false;
    }

    public LightweightCache<string, NServiceBusPostgresqlQueue> Queues { get; }

    /// <summary>
    /// Schema name for the NServiceBus queue tables. Defaults to "public", matching the
    /// NServiceBus PostgreSQL transport default.
    /// </summary>
    public string SchemaName { get; set; } = "public";

    public override Uri ResourceUri => new("nservicebus-postgresql-transport://");

    protected override IEnumerable<NServiceBusPostgresqlQueue> endpoints()
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

    /// <summary>
    ///     GH-5814. Resolves a queue by the name its Uri was built from, with its case. <c>Uri.Host</c> is lowercased
    ///     by <see cref="System.Uri" />, and a PostgreSQL quoted table name is case-sensitive, so a reply to an
    ///     NServiceBus endpoint called <c>Sales.Endpoint</c> used to be written to a <c>sales.endpoint</c> table.
    /// </summary>
    protected override NServiceBusPostgresqlQueue findEndpointByUri(Uri uri)
    {
        var name = QueueNameFor(uri);
        if (Queues.TryFind(name, out var queue))
        {
            return queue;
        }

        // A Uri rebuilt from its string form (Uri.ToString()) arrives lowercased. If the name could have been
        // lowercased that way and exactly one known queue matches it apart from case, that is the queue it means.
        if (!name.Any(char.IsUpper))
        {
            var matches = Queues.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
        }

        return Queues[name];
    }

    /// <summary>
    ///     GH-5814. The queue name a Uri made by <see cref="NServiceBusPostgresqlQueue.ToUri" /> carries, with its
    ///     case: read from <see cref="Uri.OriginalString" />, since <see cref="Uri.Host" /> is lowercased, or from the
    ///     path of the <c>queue/{escaped name}</c> form used for a name that is not a valid Uri host.
    /// </summary>
    internal static string QueueNameFor(Uri uri)
    {
        if (uri.Host == "queue" && uri.AbsolutePath.Length > 1)
        {
            return Uri.UnescapeDataString(uri.AbsolutePath[1..]);
        }

        var original = uri.OriginalString;
        var start = original.IndexOf("://", StringComparison.Ordinal);
        if (start >= 0)
        {
            start += 3;
            var end = original.IndexOfAny(['/', '?', '#'], start);
            var authority = end < 0 ? original[start..] : original[start..end];

            // Only when it is the same host apart from case, so a Uri that System.Uri rewrote is read as it reads it
            if (string.Equals(authority, uri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return authority;
            }
        }

        return uri.Host;
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
                "The NServiceBus PostgreSQL interop transport can only be used if Wolverine's message persistence is also PostgreSQL backed");
        }

        // de facto environment test
        await using var conn = await Store.NpgsqlDataSource.OpenConnectionAsync();
        await conn.CloseAsync();
    }

    internal PostgresqlMessageStore Store { get; set; } = null!;

    /// <summary>
    /// Resolve the PostgreSQL connection string used for the NServiceBus queue tables. Works
    /// before <see cref="ConnectAsync"/> has run (e.g. from the Weasel command line) by falling
    /// back to the PostgreSQL message store registered for Wolverine's own persistence.
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
            "The NServiceBus PostgreSQL interop transport requires PostgreSQL backed message persistence");
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("NServiceBus Queue");
        yield return new PropertyColumn("Count", Justify.Right);
    }
}
