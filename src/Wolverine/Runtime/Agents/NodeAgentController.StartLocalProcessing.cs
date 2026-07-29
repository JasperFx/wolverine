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

        // GH-3698: adopt the assigned number BEFORE the observer writes the NodeStarted record. NodeRecord.For
        // reads it straight off Options.Durability, which until this line still holds the per-process default
        // -- Guid.NewGuid().ToString().GetDeterministicHashCode() -- so every NodeStarted row in a Balanced
        // cluster carried a random node_number unrelated to the node it describes. The Solo path in
        // StartLocally.cs already sets it first.
        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeNumber;

        await _observer.NodeStarted();

        _logger.LogInformation("Starting agents for Node {NodeId} with assigned node id {Id} and Control Uri {ControlUri}",
            options.UniqueNodeId, current.AssignedNodeNumber,current.ControlUri);

        HasStartedLocalAgentWorkflowForBalancedMode = true;

        return AgentCommands.Empty;
    }

    public bool HasStartedLocalAgentWorkflowForBalancedMode { get; private set; }
}