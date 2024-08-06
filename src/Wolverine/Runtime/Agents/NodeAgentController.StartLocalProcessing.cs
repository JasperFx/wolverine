using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public async Task<AgentCommands> StartLocalAgentProcessingAsync(WolverineOptions options)
    {
        var current = WolverineNode.For(options);
        foreach (var controller in _agentFamilies.Values.OfType<IStaticAgentFamily>())
        {
            current.Capabilities.AddRange(await controller.SupportedAgentsAsync());
        }

        current.AssignedNodeId = await _persistence.PersistAsync(current, _cancellation.Token);
        await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStarted));

        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeId;

        _logger.LogInformation("Starting agents for Node {NodeId} with assigned node id {Id}",
            options.UniqueNodeId, current.AssignedNodeId);

        HasStartedLocalAgentWorkflowForBalancedMode = true;

        return AgentCommands.Empty;
    }

    public bool HasStartedLocalAgentWorkflowForBalancedMode { get; private set; }
}