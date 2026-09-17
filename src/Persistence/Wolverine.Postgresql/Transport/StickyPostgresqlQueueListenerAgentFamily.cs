using Wolverine.Configuration;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport;

public class StickyPostgresqlQueueListenerAgentFamily : IAgentFamily
{
    private readonly IWolverineRuntime _runtime;
    public static string StickyListenerSchema = "pg-queue-listener";
    private readonly MultiTenantedMessageStore _stores;
    private readonly PostgresqlQueue[] _queues;

    public StickyPostgresqlQueueListenerAgentFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        if (_runtime.Storage is MultiTenantedMessageStore databases)
        {
            _stores = databases;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(runtime),
                $"The message storage is not {nameof(MultiTenantedMessageStore)}");
        }

        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        _queues = transport.Queues.Where(x => x is { IsListener: true, ListenerScope: ListenerScope.Exclusive }).ToArray();
    }

    public string Scheme { get; set; } = StickyListenerSchema;
    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        await _stores.Source.RefreshAsync();

        var uris = databaseIdentifiers()
            .Distinct()
            .SelectMany(identifier => _queues.Select(q => new Uri($"{Scheme}://{q.Name}/{identifier}")))
            .ToList();

        return uris;
    }

    /// <summary>
    ///     GH-4455. One identifier per tenant database, and it has to be something that
    ///     <see cref="MultiTenantedMessageStore.GetDatabaseAsync"/> can resolve on <i>any</i> node, because that
    ///     is the only thing the agent Uri carries to whichever node the agent gets assigned to. A message
    ///     store's <c>Name</c> is NOT that: the native PostgreSQL tenancy names a tenant store after its
    ///     database Uri, so round-tripping the name sent a physical database name ("psql-tenanta-db") to an
    ///     <c>ITenantedSource</c> keyed by tenant id, and the agent failed to start forever. The tenant keys
    ///     from <c>AllActiveByTenant()</c> always resolve: Marten keys its assignments by the database
    ///     identifier that its own tenancy also answers to, and every other source keys them by tenant id.
    /// </summary>
    private IEnumerable<string> databaseIdentifiers()
    {
        // The main database is always reachable as the default tenant. Skipped when it belongs to another
        // engine -- a SQL Server main store with a PostgreSQL queue transport alongside it (#3248).
        if (_stores.Main is PostgresqlMessageStore)
        {
            yield return TransportConstants.Default;
        }

        var byDatabase = _stores.Source.AllActiveByTenant()
            .Where(x => x.Value is PostgresqlMessageStore)
            .GroupBy(x => x.Value.Name);

        foreach (var group in byDatabase)
        {
            var tenantIds = group.Select(x => x.TenantId).ToArray();

            // Still exactly one agent per database so that two tenants sharing a database cannot end up
            // listening to the same queue table from two different nodes. Prefer the identifier that is
            // already the store's name -- that is the Marten shape, and keeping it means no existing agent
            // Uri moves. Otherwise the lowest tenant id, which every node computes the same way.
            yield return tenantIds.Contains(group.Key)
                ? group.Key
                : tenantIds.OrderBy(x => x, StringComparer.Ordinal).First();
        }
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var queueName = uri.Host;
        var databaseIdentifier = uri.Segments.Last(x => x != "/").Trim('/');

        var agent = new StickyPostgresqlQueueListenerAgent(_runtime, queueName, databaseIdentifier);
        return ValueTask.FromResult<IAgent>(agent) ;
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }
}