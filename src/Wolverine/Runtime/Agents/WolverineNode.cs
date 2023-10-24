namespace Wolverine.Runtime.Agents;

public class WolverineNode
{
    public Guid Id { get; set; }
    public int AssignedNodeId { get; set; } = 1; // Important, this can NEVER be 0
    public Uri? ControlUri { get; set; }
    public string Description { get; set; } = Environment.MachineName;

    public List<Uri> Capabilities { get; } = new();

    public List<Uri> ActiveAgents { get; } = new();
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset LastHealthCheck { get; set; }

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
            Id = options.UniqueNodeId,
            ControlUri = options.Transports.NodeControlEndpoint?.Uri
        };
    }
}