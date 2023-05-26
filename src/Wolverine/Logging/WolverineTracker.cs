using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Logging;

public interface IWolverineEvent
{
    void ModifyState(WolverineTracker tracker);
}

public class WolverineTracker : WatchedObservable<IWolverineEvent>, IObserver<IWolverineEvent>, INodeStateTracker
{
    internal WolverineTracker(ILogger logger) : base(logger)
    {
        Subscribe(this);
    }

    internal LightweightCache<string, ListenerState> ListenerStateByName { get; }
        = new(name => new ListenerState("unknown://".ToUri(), name, ListeningStatus.Unknown));

    internal LightweightCache<Uri, ListenerState> ListenerStateByUri { get; }
        = new(uri => new ListenerState(uri, uri.ToString(), ListeningStatus.Unknown));

    internal Dictionary<Guid, WolverineNode> Nodes { get; } = new();

    internal Dictionary<Uri, Guid?> Agents { get; } = new();

    WolverineNode INodeStateTracker.Add(WolverineNode node)
    {
        Nodes[node.Id] = node;

        if (node.IsLeader()) Leader = node;

        foreach (var agent in node.ActiveAgents)
        {
            Agents[agent] = node.Id;
        }
        
        return node;
    }

    WolverineNode INodeStateTracker.MarkCurrent(WolverineNode node)
    {
        Self = node;
        Nodes[node.Id] = node;

        return node;
    }

    WolverineNode? INodeStateTracker.FindOldestNode()
    {
        return Nodes.Values.MinBy(x => x.AssignedNodeId);
    }
    
    IEnumerable<WolverineNode> INodeStateTracker.OtherNodes()
    {
        return Nodes.Values.Where(x => x != Self);
    }

    void INodeStateTracker.Publish(IWolverineEvent @event)
    {
        Publish(@event);
    }

    public WolverineNode Self { get; private set; }
    public WolverineNode? Leader { get; internal set; }


    void IObserver<IWolverineEvent>.OnCompleted()
    {
        // nothing
    }

    void IObserver<IWolverineEvent>.OnError(Exception error)
    {
        // nothing
    }

    void IObserver<IWolverineEvent>.OnNext(IWolverineEvent value)
    {
        value.ModifyState(this);
    }

    /// <summary>
    ///     Current status of a listener by endpoint
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    public ListeningStatus StatusFor(Uri uri)
    {
        return ListenerStateByUri[uri].Status;
    }

    /// <summary>
    ///     Current status of a listener by the endpoint name
    /// </summary>
    /// <param name="endpointState"></param>
    /// <returns></returns>
    public ListeningStatus StatusFor(string endpointState)
    {
        return ListenerStateByName[endpointState].Status;
    }

    public Task WaitForListenerStatusAsync(string endpointName, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = ListenerStateByName[endpointName];
        if (listenerState.Status == status)
        {
            return Task.CompletedTask;
        }

        var waiter =
            new ConditionalWaiter<IWolverineEvent, ListenerState>(
                state => state.EndpointName == endpointName || state.Status == status, this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }

    public Task WaitForListenerStatusAsync(Uri uri, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = ListenerStateByUri[uri];
        if (listenerState.Status == status)
        {
            return Task.CompletedTask;
        }

        var waiter =
            new ConditionalWaiter<IWolverineEvent, ListenerState>(state => state.Uri == uri || state.Status == status,
                this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }

#pragma warning disable VSTHRD200
    public Task<NodeEvent> WaitForNodeEvent(Guid nodeId, TimeSpan timeout)
#pragma warning restore VSTHRD200
    {
        var waiter = new ConditionalWaiter<IWolverineEvent, NodeEvent>(e => e.Node.Id == nodeId, this, timeout);

        return waiter.Completion;
    }
    
#pragma warning disable VSTHRD200
    public Task<NodeEvent> WaitForNodeEvent(NodeEventType type, TimeSpan timeout)
#pragma warning restore VSTHRD200
    {
        var waiter = new ConditionalWaiter<IWolverineEvent, NodeEvent>(e => e.Type == type, this, timeout);

        return waiter.Completion;
    }

    public Task<NodeEvent> WaitUntilAssumesLeadership(TimeSpan timeout)
    {
        if (IsLeader()) return Task.FromResult(new NodeEvent(Self, NodeEventType.LeadershipAssumed));
        
        var waiter = new ConditionalWaiter<IWolverineEvent, NodeEvent>(e => e.Type == NodeEventType.LeadershipAssumed && e.Node.Id == Self.Id, this, timeout);

        return waiter.Completion;
    }

    public bool IsLeader()
    {
        return Self.IsLeader();
    }

    public WolverineNode? FindOwnerOfAgent(Uri agentUri)
    {
        if (Agents.TryGetValue(agentUri, out var nodeId))
        {
            if (nodeId != null && Nodes.TryGetValue(nodeId.Value, out var node)) return node;
        }

        return Nodes.Values.FirstOrDefault(x => x.ActiveAgents.Contains(agentUri));
    }

    public void Remove(WolverineNode node)
    {
        new NodeEvent(node, NodeEventType.Exiting).ModifyState(this);

        foreach (var pair in Agents.ToArray())
        {
            if (pair.Value == node.Id)
            {
                Agents[pair.Key] = null;
            }
        }
    }

    public IEnumerable<Uri> AllAgents()
    {
        return Agents.Keys.ToArray();
    }

    void INodeStateTracker.RegisterAgents(IReadOnlyList<Uri> agents)
    {
        foreach (var agent in agents)
        {
            Agents.TryAdd(agent, null);
        }
    }

    public IEnumerable<WolverineNode> AllNodes()
    {
        return Nodes.Values;
    }

    public bool AgentIsRunning(Uri agentUri)
    {
        if (Agents.TryGetValue(agentUri, out var nodeId))
        {
            return nodeId != null;
        }

        return false;
    }
}

internal class InvalidNodeException : Exception
{
    public InvalidNodeException(Guid nodeId) : base($"Referenced node with id {nodeId} does not exist")
    {
    }
}

public record ListenerState(Uri Uri, string EndpointName, ListeningStatus Status) : IWolverineEvent
{
    public void ModifyState(WolverineTracker tracker)
    {
        tracker.ListenerStateByUri[Uri] = this;
        tracker.ListenerStateByName[EndpointName] = this;
    }
}