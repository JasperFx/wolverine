using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    private Task _soloCheckingTask = null!;

    public async Task StartSoloModeAsync()
    {
        // Scope the startup activity to the *initial* assignment work only. Critically,
        // this using block must close before the recurring loop is scheduled below --
        // see GH-3518.
        using (var activity = ShouldTraceHealthCheck()
                   ? WolverineTracing.ActivitySource.StartActivity("wolverine_node_assignments")
                   : null)
        {
            await _runtime.Storage.Nodes.ClearAllAsync(_cancellation.Token);
            await _runtime.Storage.Admin.ReleaseAllOwnershipAsync();

            var current = WolverineNode.For(_runtime.Options);

            _runtime.Options.Durability.AssignedNodeNumber = current.AssignedNodeNumber = 1;
            await _observer.NodeStarted();

            await startAllAgentsAsync();
        }

        _soloCheckingTask = startSoloHealthCheckLoop();

        HasStartedInSoloMode = true;
    }

    private Task startSoloHealthCheckLoop()
    {
        // Suppress the ambient ExecutionContext flow so the AsyncLocal behind
        // Activity.Current is NOT captured into the forked background task. Without this,
        // Task.Run snapshots whatever activity happens to be current at scheduling time
        // and every loop iteration (plus every DB call underneath it) reparents itself to
        // that one long-lived activity for the entire process lifetime -- producing a
        // single unbounded trace. See GH-3518. Each tick starts its own fresh, bounded
        // activity below, gated by ShouldTraceHealthCheck() so the sampling period is
        // actually honored in Solo mode.
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_runtime.Options.Durability.CheckAssignmentPeriod, _cancellation.Token);

                        using var activity = ShouldTraceHealthCheck()
                            ? WolverineTracing.ActivitySource.StartActivity("wolverine_node_assignments")
                            : null;

                        await startAllAgentsAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        // Just done
                    }
                }
            }, _cancellation.Token);
        }
    }

    private async Task startAllAgentsAsync()
    {
        foreach (var controller in _agentFamilies.Values)
        {
            IReadOnlyList<Uri> allAgents;
            try
            {
                allAgents = await controller.AllKnownAgentsAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents",
                    controller.Scheme);
                continue;
            }

            foreach (var uri in allAgents)
            {
                try
                {
                    // This is idempotent, so call away!
                    await StartAgentAsync(uri);
                }
                catch (Exception e)
                {
                    // GH-3519: isolate each agent's start so a single agent that cannot start --
                    // e.g. an event-subscription shard that loses a first-assignment startup race
                    // with high-water detection -- does not skip the remaining, healthy sibling
                    // agents in the same family for this reevaluation tick. The wedged agent is
                    // still retried on the next tick; it just no longer takes its siblings down
                    // with it.
                    _logger.LogError(e, "Error trying to start agent {AgentUri}", uri);
                }
            }
        }
    }
}