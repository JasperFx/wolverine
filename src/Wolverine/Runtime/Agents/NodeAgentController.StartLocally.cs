using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    private Task _soloCheckingTask;

    public async Task StartSoloModeAsync()
    {
        using var activity = WolverineTracing.ActivitySource.StartActivity("wolverine_node_assignments");
        
        await _runtime.Storage.Nodes.ClearAllAsync(_cancellation.Token);
        await _runtime.Storage.Admin.ReleaseAllOwnershipAsync();

        var current = WolverineNode.For(_runtime.Options);

        _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeNumber = 1;
        await _observer.NodeStarted();

        await startAllAgentsAsync();

        _soloCheckingTask = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_runtime.Options.Durability.CheckAssignmentPeriod, _cancellation.Token);
                    await startAllAgentsAsync();
                }
                catch (OperationCanceledException)
                {
                    // Just done
                }
            }
        }, _cancellation.Token);

        HasStartedInSoloMode = true;
    }

    private async Task startAllAgentsAsync()
    {
        foreach (var controller in _agentFamilies.Values)
        {
            try
            {
                var allAgents = await controller.AllKnownAgentsAsync();
                foreach (var uri in allAgents)
                {
                    // This is idempotent, so call away!
                    await StartAgentAsync(uri);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    controller.Scheme);
            }
        }
    }
}