using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

public class WolverineNode
{
    public string Id
    {
        get => NodeId.ToString();
        set => NodeId = Guid.Parse(value);
    }
    
    public Guid NodeId { get; set; }
    public int AssignedNodeNumber { get; set; } = 1; // Important, this can NEVER be 0
    public Uri? ControlUri { get; set; }
    public string Description { get; set; } = Environment.MachineName;

    public List<Uri> Capabilities { get; set; } = new();

    public List<Uri> ActiveAgents { get; set; } = new();

    /// <summary>
    ///     This node's self-reported load percentage (see <see cref="INodeLoadMonitor" />), refreshed
    ///     on every heartbeat. Null when the node is not advertising load, in which case the leader
    ///     treats it as always having headroom.
    /// </summary>
    public double? LoadFactor { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset LastHealthCheck { get; set; } = DateTimeOffset.UtcNow;
    
    public bool IsLeader()
    {
        return ActiveAgents.Contains(NodeAgentController.LeaderUri);
    }

    public static WolverineNode For(WolverineOptions options)
    {
        if (options.Durability.Mode == DurabilityMode.Balanced && options.Transports.NodeControlEndpoint == null)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ControlEndpoint cannot be null for this usage");
        }

        return new WolverineNode
        {
            Version = options.Version!,
            NodeId = options.UniqueNodeId,
            ControlUri = options.Transports.NodeControlEndpoint?.Uri,
            LastHealthCheck = DateTimeOffset.UtcNow
        };
    }

    public Version Version { get; set; } = new Version(0, 0, 0, 0);

    public void AssignAgents(IReadOnlyList<Uri> agents)
    {
        // GH-4886 follow-up: every heartbeat rebuilds this node's record with every agent it is running, and
        // this used to Fill() them one at a time -- a List.Contains scan per agent over the ones already
        // added, quadratic in the node's agent count. At thousands of per-tenant projection agents per node
        // that was seconds of CPU on every heartbeat of every node in the fleet. Same result, one set
        // lookup per agent.
        var known = ActiveAgents.ToHashSet();
        foreach (var agent in agents)
        {
            if (known.Add(agent))
            {
                ActiveAgents.Add(agent);
            }
        }
    }
}