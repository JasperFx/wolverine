using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public async Task StartSoloModeAsync()
    {
        var current = WolverineNode.For(_runtime.Options);

        current.AssignedNodeId = await _persistence.PersistAsync(current, _cancellation);
        await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStarted));
        
        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeId;

        foreach (var controller in _agentFamilies.Values)
        {
            try
            {
                var allAgents = await controller.AllKnownAgentsAsync();
                foreach (var uri in allAgents)
                {
                    await StartAgentAsync(uri);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    controller.Scheme);
            }
        }

        HasStartedInSoloMode = true;
    }
}