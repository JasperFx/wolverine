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
        DistributeEvenly(scheme, _ => true);
    }

    /// <summary>
    ///     Attempts to redistribute the agents of a given agent type that match <paramref name="filter" />
    ///     evenly across the known, executing nodes with minimal disruption. Agents of the scheme outside the
    ///     filter are left completely untouched, so one scheme can be distributed in several independent
    ///     passes (e.g. per event store).
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public void DistributeEvenly(string scheme, Func<Uri, bool> filter)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        // Need to weed out agents that aren't "paused"
        var agents = AvailableAgentsForScheme(scheme, filter);
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

        // Per-node counts must only consider the agents in this pass — otherwise a filtered pass
        // would detach or count agents that belong to a different pass of the same scheme.
        var agentSet = agents.ToHashSet();
        int countOn(Node node) => node.Agents.Count(agentSet.Contains);

        var spread = (double)agents.Count / _nodes.Count;
        var minimum = (int)Math.Floor(spread);
        var maximum = (int)Math.Ceiling(spread); // this is helpful to reduce the number of assignments

        // First, pair down number of running agents if necessary. Might have to steal some later
        foreach (var node in _nodes)
        {
            var extras = node.Agents.Where(agentSet.Contains).Skip(maximum).ToArray();
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

            var count = countOn(node);

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

            var node = _nodes.FirstOrDefault(x => !x.IsLeader && countOn(x) < maximum) ?? _nodes.FirstOrDefault(x => !x.IsLeader) ?? _nodes.First();
            node.Assign(agent);
        }
    }

    /// <summary>
    /// Distribute agents of a scheme across nodes with <b>group affinity</b>: all agents that share a
    /// <paramref name="groupKey"/> (e.g. a shard database) are assigned to the same node. Intended for
    /// sharded event stores whose per-(shard, tenant) agents each connect to their shard database: an even
    /// per-agent spread makes every node open pools to (nearly) every database (pools grow as
    /// nodes×databases and exhaust a shared server's max_connections), while grouping keeps each node
    /// connected only to the databases it owns, so pools scale with the number of databases
    /// (JasperFx/marten#4806).
    ///
    /// <para>Groups are placed largest-first onto the least-loaded node (deterministic tie-breaks), so total
    /// agent count stays balanced and a steady grid does not churn.</para>
    /// </summary>
    public void DistributeByGroupAffinity(string scheme, Func<Uri, string> groupKey)
    {
        DistributeByGroupAffinity(scheme, groupKey, _ => true);
    }

    /// <summary>
    ///     Same as <see cref="DistributeByGroupAffinity(string, Func{Uri, string})" />, restricted to the
    ///     agents of the scheme matching <paramref name="filter" />. Agents outside the filter are left
    ///     completely untouched so one scheme can be distributed in several independent passes.
    ///
    ///     <para>Blue/green capability matching mirrors <see cref="DistributeEvenlyWithBlueGreenSemantics(string)" />:
    ///     with homogeneous node capabilities placement is capability-blind; otherwise a group's candidate
    ///     nodes are those capable of running every agent in the group, the least-loaded candidate hosts the
    ///     group, and when no node can host the whole group each agent falls back individually to its
    ///     least-loaded capable node — an agent no node declares a capability for is left exactly as the even
    ///     path leaves it (running where it is, or unassigned).</para>
    /// </summary>
    public void DistributeByGroupAffinity(string scheme, Func<Uri, string> groupKey, Func<Uri, bool> filter)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        // Mirror DistributeEvenlyWithBlueGreenSemantics: identical capabilities everywhere means placement
        // is capability-blind; otherwise match each agent to the nodes that declare it as a capability.
        var sameCapabilities = AllNodesHaveSameCapabilities(scheme, filter);

        var agents = sameCapabilities
            ? AvailableAgentsForScheme(scheme, filter)
            : MatchAgentsToCapableNodesFor(scheme, filter);

        if (agents.Count == 0)
        {
            return;
        }

        var nodes = _nodes.OrderBy(x => x.IsLeader).ThenBy(x => x.AssignedId).ToList();

        if (nodes.Count == 1)
        {
            // Same single-node behavior as both even paths: the only node takes everything.
            var only = nodes[0];
            foreach (var agent in agents)
            {
                only.Assign(agent);
            }

            return;
        }

        var load = nodes.ToDictionary(n => n, _ => 0);

        // Mirror the even paths' per-node ceiling so groups spread instead of piling up on the node that
        // happens to be running them today. A single group larger than the ceiling still occupies one
        // node whole — groups are indivisible by design.
        var maximum = (int)Math.Ceiling((double)agents.Count / nodes.Count);

        var groups = agents
            .GroupBy(a => groupKey(a.Uri))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in groups)
        {
            var members = group.ToList();

            // Candidate nodes for the whole group: nodes capable of running every member (all nodes when
            // capabilities are homogeneous) — plus any node that was already running part of the group
            // when the grid was assembled. The grandfathering mirrors the even paths, which leave running
            // agents in place regardless of declared capabilities: a node's capability snapshot is
            // persisted once at node startup, so a node that started before (say) a tenant database was
            // provisioned never declares that database's agents even though it is happily running them.
            var candidates = sameCapabilities
                ? nodes
                : nodes.Where(n => members.All(m => m.CandidateNodes.Contains(n))
                                   || members.Any(m => m.OriginalNode == n)).ToList();

            if (candidates.Count == 0)
            {
                // No single node can host the whole group — mirror the blue/green even path's per-agent
                // behavior: an already-running member stays where it is (minimal disruption), an
                // unassigned member goes to its least-loaded capable node, and a member no node declares
                // a capability for is simply left alone (candidate == null there too).
                foreach (var member in members)
                {
                    if (member.AssignedNode != null)
                    {
                        load[member.AssignedNode] = load.GetValueOrDefault(member.AssignedNode) + 1;
                        continue;
                    }

                    var candidate = member.CandidateNodes
                        .OrderBy(n => load.GetValueOrDefault(n))
                        .ThenBy(n => n.IsLeader)
                        .ThenBy(n => n.AssignedId)
                        .FirstOrDefault();

                    if (candidate != null)
                    {
                        candidate.Assign(member);
                        load[candidate] += 1;
                    }
                }

                continue;
            }

            // Minimal disruption, mirroring DistributeEvenly: the node already running the WHOLE group
            // keeps it as long as that doesn't push the node past the ceiling. Without this, every
            // evaluation reshuffles groups from scratch and a node whose stale capability snapshot keeps
            // it out of the capability candidates can be starved permanently across evaluations.
            var incumbent = members[0].AssignedNode;
            if (incumbent != null && members.Any(m => m.AssignedNode != incumbent))
            {
                incumbent = null;
            }

            if (incumbent != null && candidates.Contains(incumbent) &&
                load[incumbent] + members.Count <= maximum)
            {
                load[incumbent] += members.Count;
                continue;
            }

            // Otherwise the least-loaded candidate hosts the whole group (tie-breaks: non-leader first,
            // then node id).
            var node = candidates
                .OrderBy(n => load[n])
                .ThenBy(n => n.IsLeader)
                .ThenBy(n => n.AssignedId)
                .First();

            foreach (var agent in members)
            {
                node.Assign(agent);
            }

            load[node] += members.Count;
        }
    }

    public bool AllNodesHaveSameCapabilities(string scheme)
    {
        return AllNodesHaveSameCapabilities(scheme, _ => true);
    }

    /// <summary>
    ///     Same as <see cref="AllNodesHaveSameCapabilities(string)" />, only comparing the capabilities of the
    ///     scheme matching <paramref name="filter" /> — so one store's homogeneous capabilities aren't judged
    ///     "different" because another store of the same scheme is mid blue/green rollout.
    /// </summary>
    public bool AllNodesHaveSameCapabilities(string scheme, Func<Uri, bool> filter)
    {
        var gold = _nodes[0].OrderedCapabilitiesForScheme(scheme).Where(filter);

        foreach (var node in _nodes.Skip(1))
        {
            var matching = node.OrderedCapabilitiesForScheme(scheme).Where(filter);

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
        DistributeEvenlyWithBlueGreenSemantics(scheme, _ => true);
    }

    /// <summary>
    ///     Same as <see cref="DistributeEvenlyWithBlueGreenSemantics(string)" />, restricted to the agents of
    ///     the scheme matching <paramref name="filter" />. Agents outside the filter are left completely
    ///     untouched so one scheme can be distributed in several independent passes.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public void DistributeEvenlyWithBlueGreenSemantics(string scheme, Func<Uri, bool> filter)
    {
        var nodes = _nodes;
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        if (AllNodesHaveSameCapabilities(scheme, filter))
        {
            DistributeEvenly(scheme, filter);
            return;
        }

        var agents = MatchAgentsToCapableNodesFor(scheme, filter);

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

        // Per-node counts must only consider the agents in this pass; see DistributeEvenly above.
        var agentSet = agents.ToHashSet();
        int countOn(Node node) => node.Agents.Count(agentSet.Contains);

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
                .FirstOrDefault(x => countOn(x) < minimum)
                            // Or fall back to the least loaded down node
                            ?? agent.CandidateNodes.MinBy(countOn);

            candidate?.Assign(agent);
        }
    }

}
