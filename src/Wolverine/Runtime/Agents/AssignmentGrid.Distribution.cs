using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

public partial class AssignmentGrid
{
    /// <summary>
    ///     Attempts to redistribute agents for a given agent type evenly
    ///     across the known, executing nodes with minimal disruption
    /// </summary>
    /// <param name="scheme"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void DistributeEvenly(string scheme)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        // Need to weed out agents that aren't "paused"
        var agents = AvailableAgentsForScheme(scheme);
        if (agents.Count == 0)
        {
            return;
        }

        if (_nodes.Count == 1)
        {
            var node = _nodes.Single();
            foreach (var agent in agents)
            {
                node.Assign(agent);
            }

            return;
        }

        var spread = (double)agents.Count / _nodes.Count;
        var minimum = (int)Math.Floor(spread);
        var maximum = (int)Math.Ceiling(spread); // this is helpful to reduce the number of assignments

        // First, pair down number of running agents if necessary. Might have to steal some later
        foreach (var node in _nodes)
        {
            var extras = node.ForScheme(scheme).Skip(maximum).ToArray();
            foreach (var agent in extras)
            {
                agent.Detach();
            }
        }

        var missing = new Queue<Agent>(agents.Where(x => x.AssignedNode == null));

        // 2nd pass
        foreach (var node in _nodes)
        {
            if (missing.Count == 0)
            {
                break;
            }

            var count = node.ForScheme(scheme).Count();

            for (var i = 0; i < minimum - count; i++)
            {
                if (missing.Count == 0)
                {
                    break;
                }

                var agent = missing.Dequeue();
                node.Assign(agent);
            }
        }

        // Last pass for remainders
        while (missing.Count != 0)
        {
            var agent = missing.Dequeue();

            var node = _nodes.FirstOrDefault(x => !x.IsLeader && x.ForScheme(scheme).Count() < maximum) ?? _nodes.FirstOrDefault(x => !x.IsLeader) ?? _nodes.First();
            node.Assign(agent);
        }
    }

    /// <summary>
    /// Distribute agents of a scheme across nodes with <b>group affinity</b>: every agent whose
    /// <paramref name="groupKey"/> is equal is placed on the same node, and whole groups are spread
    /// evenly across nodes. Intended for sharded event stores where each agent's group is its shard
    /// database (JasperFx/marten#4806) — co-locating a database's agents on one node bounds that node
    /// to the databases it owns, so connection pools scale with databases rather than nodes × databases.
    ///
    /// <para>Balanced by a greedy least-loaded fit: each database group (largest first) goes to the node
    /// with the fewest agents so far, so nodes end up with roughly equal <em>total agent counts</em> — a
    /// proxy for per-tenant work, since a database contributes one agent per resident tenant × projection —
    /// rather than merely an equal number of databases. It balances by agent/tenant count, not by raw event
    /// volume, so a database that is few-tenants-but-huge can still be heavier than its agent count implies.
    /// Deterministic tie-breaks (prefer non-leaders, then node id) keep a steady grid from churning. This
    /// prototype does not yet apply blue/green capability matching; use the standard
    /// <see cref="DistributeEvenlyWithBlueGreenSemantics"/> when that is required.</para>
    /// </summary>
    public void DistributeByGroupAffinity(string scheme, Func<Uri, string> groupKey)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        var agents = AvailableAgentsForScheme(scheme);
        if (agents.Count == 0)
        {
            return;
        }

        var nodes = _nodes.OrderBy(x => x.IsLeader).ThenBy(x => x.AssignedId).ToList();

        if (nodes.Count == 1)
        {
            var only = nodes[0];
            foreach (var agent in agents)
            {
                only.Assign(agent);
            }

            return;
        }

        var groups = agents
            .GroupBy(a => groupKey(a.Uri))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var agentCountByNode = nodes.ToDictionary(n => n, _ => 0);
        foreach (var group in groups)
        {
            // Least-loaded node wins; prefer non-leaders and lower node ids on ties for a stable result.
            var node = agentCountByNode
                .OrderBy(kv => kv.Value)
                .ThenBy(kv => kv.Key.IsLeader)
                .ThenBy(kv => kv.Key.AssignedId)
                .First().Key;

            foreach (var agent in group)
            {
                node.Assign(agent);
            }

            agentCountByNode[node] += group.Count();
        }
    }

    public bool AllNodesHaveSameCapabilities(string scheme)
    {
        var gold = _nodes[0].OrderedCapabilitiesForScheme(scheme);

        foreach (var node in _nodes.Skip(1))
        {
            var matching = node.OrderedCapabilitiesForScheme(scheme);

            if (!gold.SequenceEqual(matching))
            {
                return false;
            }
        }

        return true;
    }
    
    /// <summary>
    /// Attempts to redistribute agents for a given agent type evenly
    /// across the known, executing nodes with minimal disruption. This version assumes
    /// that there is some blue/green deployment capability matching
    /// </summary>
    /// <param name="scheme"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void DistributeEvenlyWithBlueGreenSemantics(string scheme)
    {
        var nodes = _nodes;
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        if (AllNodesHaveSameCapabilities(scheme))
        {
            DistributeEvenly(scheme);
            return;
        }

        var agents = MatchAgentsToCapableNodesFor(scheme);

        if (agents.Count == 0)
        {
            return;
        }

        if (nodes.Count == 1)
        {
            var node = nodes.Single();
            foreach (var agent in agents) node.Assign(agent);

            return;
        }

        var spread = (double)agents.Count / nodes.Count;
        var minimum = (int)Math.Floor(spread);
        var maximum = (int)Math.Ceiling(spread); // this is helpful to reduce the number of assignments

        // First, pair down number of running agents if necessary. Might have to steal some later
        foreach (var node in nodes)
        {
            var extras = node.ForCurrentlyAssigned(agents).Skip(maximum).ToArray();
            foreach (var agent in extras)
            {
                agent.Detach();
            }
        }
        
        // In the missing, we're going to put the agents up top that can be supported in fewer places 
        var missing = agents.Where(x => x.AssignedNode == null).OrderBy(x => x.CandidateNodes.Count).ToList();
        foreach (var agent in missing)
        {
            // First try to find a node that has less than the minimum number of nodes
            var candidate = agent
                .CandidateNodes
                .FirstOrDefault(x => x.ForScheme(scheme).Count() < minimum) 
                            // Or fall back to the least loaded down node
                            ?? agent.CandidateNodes.MinBy(x => x.ForScheme(scheme).Count());

            candidate?.Assign(agent);
        }
    }

}