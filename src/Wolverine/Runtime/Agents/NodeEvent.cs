using System.Text;
using Newtonsoft.Json;
using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

internal record RemoteNodeEvent(WolverineNode Node, NodeEventType Type, WolverineNode Leader) : IAgentCommand, ISerializable
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        await runtime.Agents.InvokeAsync(Leader.Id, new NodeEvent(Node, Type));
        return AgentCommands.Empty;
    }

    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
    }

    public static object Read(byte[] bytes)
    {
        return JsonConvert.DeserializeObject<RemoteNodeEvent>(Encoding.UTF8.GetString(bytes));
    }
}

/// <summary>
///     Records a change in state for the active nodes within this Wolverine system
/// </summary>
/// <param name="Node"></param>
/// <param name="Type"></param>
public record NodeEvent(WolverineNode Node, NodeEventType Type) : IWolverineEvent, IAgentCommand, ISerializable
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        return runtime.Agents.ApplyNodeEvent(this);
    }

    public virtual bool Equals(NodeEvent? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Node.Equals(other.Node) && Type == other.Type;
    }

    public void ModifyState(WolverineTracker tracker)
    {
        switch (Type)
        {
            case NodeEventType.Started:
                tracker.Nodes[Node.Id] = Node;
                break;

            case NodeEventType.Exiting:
                if (tracker.Nodes.TryGetValue(Node.Id, out var existing))
                {
                    foreach (var uri in existing.ActiveAgents)
                    {
                        tracker.Agents.TryRemove(uri, out var value);
                    }
                }

                foreach (var uri in Node.ActiveAgents) tracker.Agents[uri] = null;

                tracker.Nodes.Remove(Node.Id);

                if (tracker.Leader?.Id == Node.Id)
                {
                    tracker.Leader = null;
                }

                break;

            case NodeEventType.LeadershipAssumed:
                tracker.Leader?.ActiveAgents.Remove(NodeAgentController.LeaderUri);

                tracker.Nodes[Node.Id] = Node;
                tracker.Leader = Node;
                Node.ActiveAgents.Add(NodeAgentController.LeaderUri);
                break;
        }
    }
    
    public byte[] Write()
    {
        return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this));
    }

    public static object Read(byte[] bytes)
    {
        return JsonConvert.DeserializeObject<NodeEvent>(Encoding.UTF8.GetString(bytes));
    }

    public override string ToString()
    {
        return $"Node: {Node.Id}, {nameof(Type)}: {Type}";
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Node, (int)Type);
    }
}
