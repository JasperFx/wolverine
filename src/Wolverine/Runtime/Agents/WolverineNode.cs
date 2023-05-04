namespace Wolverine.Runtime.Agents;

public class WolverineNode
{
    public Guid Id { get; set; }
    public int AssignedNodeId { get; set; }
    public Uri ControlUri { get; set; }
    public string Description { get; set; } = Environment.MachineName;
    public DateTimeOffset? LastHealthCheck { get; set; }

    public List<Uri> Capabilities { get; } = new();

    public DateTimeOffset Started { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset HealthCheck { get; set; } = DateTimeOffset.MinValue;

    public List<Uri> ActiveAgents { get; } = new();

    public bool IsLeader() => ActiveAgents.Contains(NodeAgentController.LeaderUri);
    public bool IsLocal { get; set; }

    public static WolverineNode For(WolverineOptions options)
    {
        if (options.Transports.NodeControlEndpoint == null)
            throw new ArgumentOutOfRangeException(nameof(options), "ControlEndpoint cannot be null for this usage");
        
        return new WolverineNode
        {
            Id = options.UniqueNodeId,
            ControlUri = options.Transports.NodeControlEndpoint.Uri
        };
    }
}