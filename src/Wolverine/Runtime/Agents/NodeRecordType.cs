using System.Text.Json;

namespace Wolverine.Runtime.Agents;

public enum NodeRecordType
{
    NodeStarted,
    AgentStarted,
    NodeStopped,
    AgentStopped,
    DormantNodeEjected,
    AssignmentChanged,
    LeadershipAssumed
}

// This is marked as ISerializable so that it can go to CritterWatch w/o
// any concern about serialization settings
public class NodeRecord : ISerializable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int NodeNumber { get; set; }
    public NodeRecordType RecordType { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Description { get; set; } = string.Empty;

    public static NodeRecord For(WolverineOptions options, NodeRecordType eventType)
    {
        return new NodeRecord
        {
            NodeNumber = options.Durability.AssignedNodeNumber,
            RecordType = eventType,
            ServiceName = options.ServiceName
        };
    }

    public string ServiceName { get; set; } = string.Empty;

    public static NodeRecord For(WolverineOptions options, NodeRecordType eventType, Uri agentUri)
    {
        return new NodeRecord
        {
            NodeNumber = options.Durability.AssignedNodeNumber,
            RecordType = eventType,
            Description = agentUri.ToString(),
            ServiceName = options.ServiceName
        };
    }

    public byte[] Write()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this);
    }

    public static object Read(byte[] bytes)
    {
        return JsonSerializer.Deserialize<NodeRecord>(bytes);
    }
}