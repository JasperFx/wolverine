using Microsoft.Extensions.Logging;
using Wolverine.Persistence;

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

        // GH-3954: the durability agent family is deliberately NOT an IStaticAgentFamily -- its agent list
        // grows at runtime as tenant databases are added -- so none of its wolverinedb:// Uris pass through
        // the loop above, and the leader had no way to tell a node that runs durability agents from one with
        // Durability.DurabilityAgentEnabled = false. It assigned to both, the incapable node threw
        // "Unrecognized agent scheme 'wolverinedb'", and the assignment never converged while staged
        // envelopes sat unrecovered. Publish a single node-level marker instead; see
        // MessageStoreCollection.DurabilityCapabilityUri.
        if (_agentFamilies.ContainsKey(_runtime.Stores.Scheme))
        {
            current.Capabilities.Add(MessageStoreCollection.DurabilityCapabilityUri);
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