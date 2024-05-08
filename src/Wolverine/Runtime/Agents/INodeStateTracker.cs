using Wolverine.Logging;

namespace Wolverine.Runtime.Agents;

internal interface INodeStateTracker
{
    WolverineNode? Self { get; }

    WolverineNode? Leader { get; }

    WolverineNode Add(WolverineNode node);
    WolverineNode MarkCurrent(WolverineNode options);
    WolverineNode? FindOldestNode();

    IEnumerable<WolverineNode> OtherNodes();
    void Publish(IWolverineEvent @event);


    WolverineNode? FindOwnerOfAgent(Uri agentUri);
    void Remove(WolverineNode node);
    IEnumerable<Uri> AllAgents();
    void RegisterAgents(IReadOnlyList<Uri> agents);
    IEnumerable<WolverineNode> AllNodes();
}