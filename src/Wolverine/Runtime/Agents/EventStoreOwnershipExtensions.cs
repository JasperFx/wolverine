using JasperFx.Descriptors;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Runtime.Agents;

public static class EventStoreOwnershipExtensions
{
    /// <summary>
    /// The subset of <see cref="IEventStore.AllDatabases" /> whose event-subscription / projection agents
    /// are currently assigned to and running on THIS node under Wolverine-managed event subscription
    /// distribution (<c>UseWolverineManagedEventSubscriptionDistribution = true</c>).
    ///
    /// <para>
    /// Use this instead of <see cref="IEventStore.AllDatabases" /> for any recurring per-node work against a
    /// multi-database store — progress queries, telemetry polls, readiness gates, sweeps. Under database-affine
    /// agent assignment a node already holds open connection pools for exactly the databases it owns; scoping
    /// per-node work to those databases reuses them, while fanning out to every database opens a fresh pool per
    /// database per node (512 shard databases × N pods was enough to drive a shared database server to its
    /// connection ceiling — GH-3340, CritterWatch#791).
    /// </para>
    ///
    /// <para>Semantics:</para>
    /// <list type="bullet">
    /// <item>A store backed by zero or one database is returned as-is — there is nothing to scope.</item>
    /// <item>Under <see cref="DurabilityMode.Solo" /> every database is local: the one node owns everything,
    /// and the daemon runs in-process rather than as managed agents, so the running-agent set carries no
    /// ownership signal to read.</item>
    /// <item>When no <see cref="IEventSubscriptionAgentFamily" /> is registered — Wolverine-managed event
    /// subscription distribution is not in play — ownership does not exist as a concept and every database is
    /// returned, preserving the unscoped behavior.</item>
    /// <item>Otherwise the running managed-agent set is authoritative: each database's agents run on exactly
    /// one node, so the union of this method's results across all nodes covers every database exactly once.
    /// A node that currently owns no agents for the store gets an EMPTY list — that is the honest answer
    /// during startup or after a rebalance, so callers gating readiness can wait instead of latching early.
    /// Callers that must never go dark (monitoring sweeps) should treat empty as "fall back to all databases"
    /// themselves. Ownership shifts as Wolverine rebalances agents, so never cache the result.</item>
    /// </list>
    /// </summary>
    public static async ValueTask<IReadOnlyList<IEventDatabase>> AllLocallyOwnedDatabasesAsync(
        this IWolverineRuntime runtime, IEventStore store, CancellationToken token = default)
    {
        var all = await store.AllDatabases().ConfigureAwait(false);
        if (all.Count <= 1)
        {
            return all;
        }

        if (runtime.Options.Durability.Mode == DurabilityMode.Solo)
        {
            return all;
        }

        if (!runtime.Services.GetServices<IEventSubscriptionAgentFamily>().Any())
        {
            return all;
        }

        var ownedIds = runtime.Agents.AllRunningAgentUris()
            .Where(uri => BelongsToStore(uri, store.Identity))
            .Select(EventSubscriptionAgentFamily.DatabaseIdOf)
            .Where(id => id != null)
            .Select(id => id!)
            .ToHashSet();

        if (ownedIds.Count == 0)
        {
            return Array.Empty<IEventDatabase>();
        }

        // Map the owned DatabaseIds back to database identifiers through the store's usage descriptors —
        // the exact source EventStoreAgents built the agent URIs from, so the round trip cannot drift.
        var usage = await store.TryCreateUsage(token).ConfigureAwait(false);
        if (usage?.Database == null)
        {
            // No usage descriptor to map through: fail open rather than silently dropping databases.
            return all;
        }

        var ownedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in usage.Database.Databases)
        {
            if (ownedIds.Contains(new DatabaseId(descriptor.ServerName, descriptor.DatabaseName)))
            {
                ownedIdentifiers.Add(descriptor.Identifier);
            }
        }

        if (usage.Database.MainDatabase is { } main &&
            ownedIds.Contains(new DatabaseId(main.ServerName, main.DatabaseName)))
        {
            ownedIdentifiers.Add(main.Identifier);
        }

        return all.Where(db => ownedIdentifiers.Contains(db.Identifier)).ToList();
    }

    // Agent URIs are event-subscriptions://{storeType}/{storeName}/{databaseId}/{shard...}
    // (EventSubscriptionAgentFamily.UriFor); the (type, name) prefix identifies the owning store.
    private static bool BelongsToStore(Uri uri, EventStoreIdentity identity)
    {
        if (uri.Scheme != EventSubscriptionAgentFamily.SchemeName || uri.Segments.Length < 3)
        {
            return false;
        }

        return uri.Host.Equals(identity.Type, StringComparison.OrdinalIgnoreCase)
               && uri.Segments[1].Trim('/').Equals(identity.Name, StringComparison.OrdinalIgnoreCase);
    }
}
