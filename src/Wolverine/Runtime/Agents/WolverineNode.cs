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
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset LastHealthCheck { get; set; } = DateTimeOffset.Now;
    
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
            Version = options.Version,
            NodeId = options.UniqueNodeId,
            ControlUri = options.Transports.NodeControlEndpoint?.Uri,
            LastHealthCheck = DateTimeOffset.UtcNow
        };
    }

    public Version Version { get; set; } = new Version(0, 0, 0, 0);

    public void AssignAgents(IReadOnlyList<Uri> agents)
    {
        foreach (var agent in agents)
        {
            ActiveAgents.Fill(agent);
        }
    }
}