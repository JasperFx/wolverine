namespace Wolverine.Runtime.Agents;

public enum NodeRecordType
{
    NodeStarted,
    AgentStarted,
    NodeStopped,
    AgentStopped,
    DormantNodeEjected,
    AssignmentChanged,
    LeadershipAssumed,
    ElectionRequested
}

public class NodeRecord
{
    public int NodeNumber { get; set; }
    public NodeRecordType RecordType { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Description { get; set; } = string.Empty;

    public static NodeRecord For(WolverineOptions options, NodeRecordType eventType)
    {
        return new NodeRecord
        {
            NodeNumber = options.Durability.AssignedNodeNumber,
            RecordType = eventType
        };
    }

    public static NodeRecord Starting(WolverineOptions options, NodeRecordType eventType)
    {
        return new NodeRecord
        {
            NodeNumber = options.Durability.AssignedNodeNumber,
            RecordType = eventType,
            Description = $"UniqueNodeId: {options.UniqueNodeId}, MachineName: {Environment.MachineName}"
        };
    }

    public static NodeRecord For(WolverineOptions options, NodeRecordType eventType, Uri agentUri)
    {
        return new NodeRecord
        {
            NodeNumber = options.Durability.AssignedNodeNumber,
            RecordType = eventType,
            Description = agentUri.ToString()
        };
    }
}