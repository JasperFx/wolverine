using Microsoft.Extensions.Logging;
using Wolverine.Util;

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
        // Detached so the AsyncLocal behind Activity.Current is NOT captured into the forked
        // background task. Without this, Task.Run snapshots whatever activity happens to be
        // current at scheduling time and every loop iteration (plus every DB call underneath
        // it) reparents itself to that one long-lived activity for the entire process
        // lifetime -- producing a single unbounded trace. See GH-3518; the helper is GH-4650's
        // generalisation of the same fix. Each tick starts its own fresh, bounded activity
        // below, gated by ShouldTraceHealthCheck() so the sampling period is actually honored
        // in Solo mode.
        return DetachedTask.Run(async () =>
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

    private async Task startAllAgentsAsync()
    {
        // GH-4672. Solo mode has no assignment grid, so nothing here enforced the paused restrictions that
        // AssignmentGrid.ApplyRestrictions enforces in Balanced. This loop runs every CheckAssignmentPeriod
        // and started every URI a family knows about, so an operator's pause was undone within one period
        // of StopLocallyAsync -- a pause could not be made to stick in a single-instance deployment at all.
        //
        // Read through Storage.Nodes, NOT this controller's own _persistence. They are not the same
        // object: in a Solo host _persistence is a NullNodeAgentPersistence, so a read there answers "no
        // restrictions" forever -- which is how the first cut of this fix changed nothing at all.
        // Storage.Nodes is where ApplyRestrictionsAsync writes the durable row, so it is the one source
        // that agrees with what the operator was told was persisted.
        var paused = await loadPausedAgentUrisAsync();

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
                // GH-4672: an operator's pause outranks the family's "everything I know about" answer.
                // Deliberately only skips the START -- stopping an agent that is already running is
                // StopLocallyAsync's job, and doing it from this loop would stop agents an operator
                // paused while they were mid-work, without being asked to.
                if (paused.Contains(uri))
                {
                    continue;
                }

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

    /// <summary>
    ///     GH-4672: the agent URIs an operator has paused, for the Solo startup loop.
    /// </summary>
    /// <remarks>
    ///     A read that fails returns nothing rather than throwing, which deliberately degrades to the
    ///     pre-GH-4672 behaviour of starting everything. Starting an agent an operator wanted paused is a
    ///     nuisance they can repeat the pause for; refusing to start ANY agent because one query failed
    ///     takes a whole single-instance deployment down with it.
    /// </remarks>
    private async Task<HashSet<Uri>> loadPausedAgentUrisAsync()
    {
        try
        {
            var state = await _runtime.Storage.Nodes.LoadNodeAgentStateAsync(_cancellation.Token);
            return state.Restrictions.FindPausedAgentUris().ToHashSet();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to load agent restrictions while starting agents locally");
            return [];
        }
    }
}