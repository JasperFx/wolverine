using System.Text.Json;
using System.Text.Json.Serialization;

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
    ListenerLatched,

    /// <summary>
    /// A node that thought it was the leader detected that its underlying
    /// advisory lock had been released server-side (network blip, idle-cull,
    /// pg_terminate_backend, AlwaysOn failover, etc.) and stepped down so a
    /// new leadership election could happen. See GH-2602.
    /// </summary>
    LeadershipLost,

    /// <summary>
    /// A locally-owned agent stopped running because of a failure it reported rather
    /// than because anything asked it to stop — an event-subscription shard the daemon
    /// paused on an <c>ApplyEventException</c> being the case this exists for. The
    /// record's Description carries the classified reason (category, failing event,
    /// root exception) so the failure is readable after the fact, and from another
    /// process, instead of only in this node's logs. See GH-3637 / GH-3638.
    /// </summary>
    AgentPaused,

    /// <summary>
    /// A node released one of its own agents after exhausting the node-local auto-restart budget on a
    /// stall that never advanced, so the leader could place the agent on a healthy peer advertising
    /// the same capability. The record's Description carries the last classified failure when the
    /// agent reported one. See GH-3888.
    /// </summary>
    AgentReleased
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
            ServiceName = options.ServiceName,
            AgentUri = agentUri
        };
    }

    public Uri AgentUri { get; set; } = new("none://");

    public byte[] Write()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, NodeRecordJsonContext.Default.NodeRecord);
    }

    public static object Read(byte[] bytes)
    {
        return JsonSerializer.Deserialize(bytes, NodeRecordJsonContext.Default.NodeRecord)!;
    }
}

/// <summary>
/// Source-generated JSON context for <see cref="NodeRecord"/>. Lets <c>Write</c> /
/// <c>Read</c> use the AOT-friendly <c>JsonTypeInfo</c> overloads instead of the
/// reflection-based <c>JsonSerializer</c> defaults — clearing IL2026/IL3050 in
/// trim/AOT builds without leaf-site suppression. NodeRecord ships only the
/// statically-known properties on the type above; if new properties are added
/// in the future, the source generator picks them up automatically.
/// </summary>
[JsonSerializable(typeof(NodeRecord))]
internal partial class NodeRecordJsonContext : JsonSerializerContext
{
}