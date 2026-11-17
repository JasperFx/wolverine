using JasperFx.Core;
using Wolverine.Persistence;

namespace Wolverine.Runtime.Agents;

public partial class AssignmentGrid
{
    private readonly Dictionary<string, NodeWorkloads> _requiredWorkloads = new(StringComparer.OrdinalIgnoreCase);

    // GH-5601: while a distribution pass runs, the nodes that run its scheme's workload. Null outside a pass, and
    // also null inside one where every node is eligible, so a cluster that never sets Workloads distributes over
    // exactly the node list it always did.
    private IReadOnlyList<Node>? _pool;

    /// <summary>
    ///     The nodes the distribution pass in progress may place agents on.
    /// </summary>
    private IReadOnlyList<Node> pool => _pool ?? _nodes;

    /// <summary>
    ///     GH-5601. Declare the workload a node has to run (<see cref="DurabilitySettings.Workloads" />) to be given the
    ///     agents of <paramref name="scheme" />. The leader declares every family's
    ///     <see cref="IAgentFamily.RequiredWorkload" /> before the families distribute.
    /// </summary>
    public void RequireWorkload(string scheme, NodeWorkloads workload)
    {
        _requiredWorkloads[scheme] = workload;
    }

    /// <summary>
    ///     GH-5601. The workload a node has to run to be given the agents of <paramref name="scheme" />. A scheme no
    ///     family declared is the durability agents' or the event subscriptions' when it is theirs, and messaging work
    ///     otherwise, the same default <see cref="IAgentFamily.RequiredWorkload" /> has.
    /// </summary>
    public NodeWorkloads RequiredWorkloadFor(string scheme)
    {
        if (_requiredWorkloads.TryGetValue(scheme, out var workload)) return workload;

        if (scheme.EqualsIgnoreCase(PersistenceConstants.AgentScheme)) return NodeWorkloads.Durability;
        if (scheme.EqualsIgnoreCase(EventSubscriptionAgentFamily.SchemeName)) return NodeWorkloads.Projections;

        return NodeWorkloads.Messaging;
    }

    /// <summary>
    ///     GH-5601. The nodes that run the workload <paramref name="scheme" />'s agents need, in the grid's order.
    /// </summary>
    public IReadOnlyList<Node> NodesFor(string scheme)
    {
        var required = RequiredWorkloadFor(scheme);
        return _nodes.Where(x => x.Runs(required)).ToList();
    }

    /// <summary>
    ///     GH-5601. Narrow the grid to the nodes running the workload of <paramref name="scheme" /> for the length of
    ///     one distribution pass, before anything else in the pass looks at a node. A workload is a hard requirement,
    ///     like a declared capability: an agent of the pass sitting on a node outside the workload is detached so the
    ///     pass can move it, a pinned one included (the GH-4591 rule for capability), because the node would refuse
    ///     to start it. When no node runs the workload, nothing is placed and every agent of the pass says why.
    /// </summary>
    /// <returns>
    ///     False when no node runs the workload and the pass has nothing to do. Otherwise true, and
    ///     <paramref name="scope" /> restores the full node list when disposed (null when nothing was narrowed).
    /// </returns>
    private bool tryNarrowToWorkload(string scheme, Func<Uri, bool> filter, out IDisposable? scope)
    {
        scope = null;

        // A pass that calls another public entry point (blue/green falling back to the even spread) is already
        // narrowed by the outer one
        if (_pool != null) return true;

        var eligible = NodesFor(scheme);
        if (eligible.Count == _nodes.Count) return true;

        foreach (var agent in _agents.Values)
        {
            if (agent.AssignedNode != null && agent.Uri.Scheme.EqualsIgnoreCase(scheme) && filter(agent.Uri) &&
                !eligible.Contains(agent.AssignedNode))
            {
                agent.Detach();
            }
        }

        if (eligible.Count == 0)
        {
            foreach (var agent in AvailableAgentsForScheme(scheme, filter))
            {
                agent.UnplacedReason = UnplacedReason.NoNodeForWorkload;
            }

            return false;
        }

        _pool = eligible;
        scope = new PoolScope(this);
        return true;
    }

    private sealed class PoolScope(AssignmentGrid grid) : IDisposable
    {
        public void Dispose() => grid._pool = null;
    }

    /// <summary>
    ///     GH-5601. Run <paramref name="pass" /> over the nodes running <paramref name="scheme" />'s workload only.
    /// </summary>
    private void withinWorkload(string scheme, Func<Uri, bool> filter, Action pass)
    {
        if (!tryNarrowToWorkload(scheme, filter, out var scope)) return;

        using (scope)
        {
            pass();
        }
    }
}
