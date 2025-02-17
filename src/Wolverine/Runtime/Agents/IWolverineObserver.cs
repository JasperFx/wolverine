namespace Wolverine.Runtime.Agents;

public interface IWolverineObserver
{
    Task AssumedLeadership();
    Task NodeStarted();
    Task NodeStopped();
    Task AgentStarted(Uri agentUri);
    Task AgentStopped(Uri agentUri);

    // Loop through and decide what you want here. 
    Task AssignmentsChanged(AssignmentGrid grid, IEnumerable<IAgentCommand> commands);
    
    // TODO -- more for listener stopped/started
    Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes);
    Task RuntimeIsFullyStarted();
}

internal class PersistenceWolverineObserver : IWolverineObserver
{
    private readonly IWolverineRuntime _runtime;

    public PersistenceWolverineObserver(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task AssumedLeadership()
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options,
            NodeRecordType.LeadershipAssumed, NodeAgentController.LeaderUri));
    }

    public async Task NodeStarted()
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStarted));
    }

    public async Task NodeStopped()
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStopped));
    }

    public async Task AgentStarted(Uri agentUri)
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.AgentStarted,
            agentUri));
    }

    public async Task AgentStopped(Uri agentUri)
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.AgentStopped,
            agentUri));
    }

    public async Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        var records = staleNodes.Select(x => new NodeRecord
        {
            NodeNumber = x.AssignedNodeNumber,
            RecordType = NodeRecordType.DormantNodeEjected,
            Description = "Health check on Node " + _runtime.Options.Durability.AssignedNodeNumber
        }).ToArray();

        await _runtime.Storage.Nodes.LogRecordsAsync(records);
    }

    public Task RuntimeIsFullyStarted()
    {
        return Task.CompletedTask;
    }

    public async Task AssignmentsChanged(AssignmentGrid grid, IEnumerable<IAgentCommand> commands)
    {
        var records = commands.Select(x => new NodeRecord
        {
            NodeNumber = _runtime.Options.Durability.AssignedNodeNumber,
            RecordType = NodeRecordType.AssignmentChanged,
            Description = x.ToString()
        }).ToArray();

        await _runtime.Storage.Nodes.LogRecordsAsync(records);
    }
}