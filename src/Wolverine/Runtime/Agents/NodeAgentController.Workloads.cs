using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    /// <summary>
    ///     GH-5601. The host of the node-level marker a node publishes in its capabilities when it runs anything other
    ///     than <see cref="NodeWorkloads.All" />: <c>wolverine://node-workloads/messaging+projections</c>, or
    ///     <c>wolverine://node-workloads/none</c> for an HTTP-only node. Like <see cref="WarmupRoleUri" /> it is a
    ///     marker, not an agent, so no node table changes, and a node without it reads as
    ///     <see cref="NodeWorkloads.All" /> -- which is what every 6.x node is.
    /// </summary>
    public const string WorkloadsMarkerHost = "node-workloads";

    /// <summary>
    ///     GH-5601. The marker a node with <paramref name="workloads" /> publishes, or null for
    ///     <see cref="NodeWorkloads.All" />, which publishes nothing.
    /// </summary>
    public static Uri? WorkloadsMarkerFor(NodeWorkloads workloads)
    {
        if (workloads == NodeWorkloads.All) return null;

        var text = workloads == NodeWorkloads.None
            ? "none"
            : Enum.GetValues<NodeWorkloads>()
                .Where(x => x != NodeWorkloads.None && x != NodeWorkloads.All && workloads.HasFlag(x))
                .Select(x => x.ToString().ToLowerInvariant())
                .Join("+");

        return new Uri($"{LeaderUri.Scheme}://{WorkloadsMarkerHost}/{text}");
    }

    /// <summary>
    ///     GH-5601. The workloads a node runs, read from its capabilities. No marker means
    ///     <see cref="NodeWorkloads.All" />. A word a newer version of Wolverine might add is ignored.
    /// </summary>
    public static NodeWorkloads WorkloadsOf(IEnumerable<Uri> capabilities)
    {
        var marker = capabilities.FirstOrDefault(x =>
            x.Scheme == LeaderUri.Scheme && x.Host.EqualsIgnoreCase(WorkloadsMarkerHost));

        if (marker == null) return NodeWorkloads.All;

        var workloads = NodeWorkloads.None;
        foreach (var word in marker.AbsolutePath.Trim('/').Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<NodeWorkloads>(word, true, out var workload))
            {
                workloads |= workload;
            }
        }

        return workloads;
    }

    /// <summary>
    ///     GH-5601. Everything this node advertises to the leader at startup: the agents of every static family
    ///     whose workload this node runs, and the node-level markers -- its workloads, the warm-up role, and
    ///     whether it can hold durability agents.
    /// </summary>
    internal static List<Uri> BuildCapabilities(DurabilitySettings durability,
        IEnumerable<(IAgentFamily Family, IReadOnlyList<Uri> Supported)> staticFamilies)
    {
        var capabilities = new List<Uri>();

        // GH-5601: an agent this node will refuse to start is not advertised, so a capability-matched placement
        // or an operator's pin can't land it here, and a leader that predates workloads keeps it away too. A
        // warm-up node keeps advertising everything it can build: its own rules already keep it to projections.
        foreach (var (family, supported) in staticFamilies)
        {
            if (durability.NodeRole == NodeRole.Warmup || durability.Runs(family.RequiredWorkload))
            {
                capabilities.AddRange(supported);
            }
        }

        // GH-3954: the durability agent family is deliberately NOT an IStaticAgentFamily -- its agent list
        // grows at runtime as tenant databases are added -- so none of its wolverinedb:// Uris pass through
        // the loop above, and the leader had no way to tell a node that runs durability agents from one with
        // Durability.DurabilityAgentEnabled = false. It assigned to both, the incapable node threw
        // "Unrecognized agent scheme 'wolverinedb'", and the assignment never converged while staged
        // envelopes sat unrecovered. Publish a single node-level marker instead; see
        // MessageStoreCollection.DurabilityCapabilityUri.
        //
        // GH-4569: a warm-up node never recovers or schedules envelopes, so it never advertises the durability
        // marker -- which keeps durability agents off it even under a leader that predates node roles -- and
        // advertises its role instead. The role rides on the capability list rather than a column of its own
        // so no store's node table changes; see NodeAgentController.WarmupRoleUri.
        //
        // GH-5601: likewise a node without the Durability workload, so a 6.x leader (GH-3954 and later) keeps
        // durability agents off it as well.
        if (durability.NodeRole == NodeRole.Warmup)
        {
            capabilities.Add(WarmupRoleUri);
        }
        else if (durability.DurabilityAgentEnabled && durability.Runs(NodeWorkloads.Durability))
        {
            capabilities.Add(MessageStoreCollection.DurabilityCapabilityUri);
        }

        if (WorkloadsMarkerFor(durability.Workloads) is { } marker)
        {
            capabilities.Add(marker);
        }

        return capabilities;
    }

    /// <summary>
    ///     GH-5601. Refuse to build an agent of a family whose workload this node does not run. The leader never
    ///     places one here, but a leader that predates workloads would, during a rolling upgrade.
    /// </summary>
    private void assertRunsWorkloadFor(Uri agentUri)
    {
        if (!_agentFamilies.TryGetValue(agentUri.Scheme, out var family)) return;

        var durability = _runtime.Options.Durability;
        var required = family.RequiredWorkload;
        if (durability.Runs(required)) return;

        throw new InvalidAgentException(
            $"Node {durability.AssignedNodeNumber} does not run the {required} workload " +
            $"(Durability.Workloads is '{durability.Workloads}'), so it cannot run agent {agentUri}. " +
            "A leader running Wolverine 7.0 or later never assigns it here.");
    }

    private bool _warnedNoNodeRunsMessaging;

    /// <summary>
    ///     GH-5601. Guard rail: when no live node in the table runs the Messaging workload, nothing consumes this
    ///     application's queues or runs its scheduled messages, and there is no leader either, because only a
    ///     Messaging node leads. Latched, so it is said once per transition rather than on every heartbeat.
    /// </summary>
    internal void WarnIfNoNodeRunsMessaging(IReadOnlyList<WolverineNode> liveNodes)
    {
        if (liveNodes.Count == 0 || liveNodes.Any(x => x.Workloads.HasFlag(NodeWorkloads.Messaging)))
        {
            _warnedNoNodeRunsMessaging = false;
            return;
        }

        if (_warnedNoNodeRunsMessaging) return;
        _warnedNoNodeRunsMessaging = true;

        _logger.LogWarning(
            "No live node in this Wolverine cluster runs the Messaging workload (Durability.Workloads), so nothing consumes this application's queues or runs its scheduled messages, and there is no leader to assign agents. Start at least one node with Messaging");
    }
}
