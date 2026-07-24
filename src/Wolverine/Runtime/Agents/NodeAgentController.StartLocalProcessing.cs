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

        // GH-3604 / D2: remember the capabilities so we can re-register with them if a peer ever deletes our
        // row out from under us. WolverineNode.For() alone carries none.
        _capabilities = current.Capabilities.ToArray();

        current.AssignedNodeNumber = await _persistence.PersistAsync(current, _cancellation.Token);
        
        await _observer.NodeStarted();

        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeNumber;

        _logger.LogInformation("Starting agents for Node {NodeId} with assigned node id {Id} and Control Uri {ControlUri}",
            options.UniqueNodeId, current.AssignedNodeNumber,current.ControlUri);

        HasStartedLocalAgentWorkflowForBalancedMode = true;

        return AgentCommands.Empty;
    }

    public bool HasStartedLocalAgentWorkflowForBalancedMode { get; private set; }
}