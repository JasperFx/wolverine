using JasperFx.Descriptors;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence;

/// <summary>
///     GH-3785: database affinity ACROSS agent families. The event-subscription family keeps a shard
///     database's projection agents together on one node (GH-3792 / marten#4806), but the durability agent
///     for that same database was distributed independently — so for most databases one node owned the
///     durability agent while a different node owned the projections, and the database attracted two nodes'
///     connection pools instead of one (measured on a 512-shard production cluster: 73% of databases split,
///     ~425 connections held by durability owners against databases they otherwise never touch).
///
///     <para>This computes, for a durability agent, the node that already owns that database's
///     event-subscription agents in the current assignment pass. It only works because
///     <see cref="NodeAgentController" /> evaluates the durability family AFTER the event-subscription
///     family over the same shared <see cref="AssignmentGrid" />.</para>
/// </summary>
internal static class DurabilityProjectionAffinity
{
    /// <summary>
    ///     The (server, database) identity of a durability agent URI, or null for URIs that don't carry one
    ///     (the null store, the multi-tenanted composite marker, RavenDb-style single-segment URIs).
    ///     Grammar per <c>MessageDatabase</c>: <c>wolverinedb://{engine}/{server}/{database}/{schema}</c>.
    /// </summary>
    internal static (string Server, string Database)? DatabaseOf(Uri uri)
    {
        if (!uri.Scheme.Equals(PersistenceConstants.AgentScheme, StringComparison.OrdinalIgnoreCase)
            || uri.Segments.Length < 3)
        {
            return null;
        }

        return (uri.Segments[1].Trim('/'), uri.Segments[2].Trim('/'));
    }

    /// <summary>
    ///     Build the preference function for <see cref="AssignmentGrid.DistributeEvenlyWithAffinity(string, Func{Uri, AssignmentGrid.Node})" /> from
    ///     the event-subscription assignments already present in the grid. Returns the node hosting the most
    ///     of a database's event-subscription agents (during a blue/green split a database legitimately has
    ///     one owner per version; the durability agent follows the larger side, tie-broken by node id for
    ///     determinism).
    /// </summary>
    internal static DurabilityAffinityPreference BuildPreference(AssignmentGrid assignments)
    {
        // (DatabaseId, node) -> how many of that database's event-subscription agents the node holds
        var owners = new Dictionary<DatabaseId, Dictionary<AssignmentGrid.Node, int>>();

        foreach (var agent in assignments.AgentsForScheme(EventSubscriptionAgentFamily.SchemeName))
        {
            if (agent.AssignedNode == null)
            {
                continue;
            }

            var databaseId = EventSubscriptionAgentFamily.DatabaseIdOf(agent.Uri);
            if (databaseId == null)
            {
                continue;
            }

            if (!owners.TryGetValue(databaseId, out var perNode))
            {
                owners[databaseId] = perNode = new Dictionary<AssignmentGrid.Node, int>();
            }

            perNode[agent.AssignedNode] = perNode.GetValueOrDefault(agent.AssignedNode) + 1;
        }

        if (owners.Count == 0)
        {
            return new DurabilityAffinityPreference(_ => null, 0);
        }

        var ownerOf = owners.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key.AssignedId)
                .First().Key);

        // The durability URI spells the database as raw (server, name) segments while the
        // event-subscription URI carries a DatabaseId, and the two families describe the same physical
        // database through different pipelines — so join on the database NAME when that is unambiguous,
        // and only fall back to comparing server spellings when two servers carry the same database name.
        // A miss here is never wrong, only not-better: the agent falls back to the even spread, which is
        // exactly today's behavior.
        var byName = ownerOf
            .GroupBy(pair => pair.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return new DurabilityAffinityPreference(uri =>
        {
            var database = DatabaseOf(uri);
            if (database == null)
            {
                return null;
            }

            if (!byName.TryGetValue(database.Value.Database, out var candidates))
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0].Value;
            }

            var server = normalizeServer(database.Value.Server);
            var matching = candidates
                .Where(x => normalizeServer(x.Key.Server) == server)
                .ToList();

            return matching.Count == 1 ? matching[0].Value : null;
        }, owners.Count);
    }

    // The durability URI's server segment is descriptor.ServerName.Split(',')[0] while DatabaseId.Server
    // is the unsplit descriptor value, and either side may or may not carry a port — normalize both down
    // to the bare lowercase host before comparing.
    private static string normalizeServer(string server)
    {
        var host = server.Split(',')[0];
        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            host = host[..colon];
        }

        return host.Trim().ToLowerInvariant();
    }
}
