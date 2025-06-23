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