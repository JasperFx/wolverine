using JasperFx.Core;
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
    /// <summary>
    /// The width of the <c>description</c> column in the <c>wolverine_node_records</c> table for every
    /// store that provisions a bounded one, and therefore the ceiling every description is truncated to
    /// on the way to the database. GH-4246: MySQL was the one provider that took Weasel's default string
    /// column instead of declaring a width, so it got <c>VARCHAR(255)</c> while the code assumed 500 --
    /// and an <c>AssignmentChanged</c> record, whose description is an agent command's ToString() and so
    /// carries an agent URI plus a node destination, blew past it and failed the insert. Every bounded
    /// store now declares this width, and <see cref="TruncateDescription"/> guarantees nothing wider is
    /// ever handed to one.
    /// </summary>
    public const int DescriptionLength = 1000;

    /// <summary>
    /// Clamp a node record description to <see cref="DescriptionLength"/>, marking the tail that was cut.
    /// These records are append-only diagnostics; a description long enough to overflow the column must
    /// lose its tail rather than turn the insert into a failure that takes an agent command down with it.
    /// </summary>
    public static string TruncateDescription(string? description)
    {
        if (description.IsEmpty()) return string.Empty;

        return description!.Length <= DescriptionLength
            ? description
            : description[..(DescriptionLength - 3)] + "...";
    }

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