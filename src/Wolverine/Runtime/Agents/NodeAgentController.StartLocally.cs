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

    // GH-5458: the Solo loop and an operator's ApplyRestrictionsAsync both run the pass below. Serialized so a
    // loop tick that read the restrictions just before a pause was persisted cannot start the agent again
    // after the operator's pass has stopped it.
    private readonly SemaphoreSlim _soloPassLock = new(1, 1);

    /// <summary>
    ///     GH-5458. Carries the persisted restrictions out on a Solo node right away: stops every running agent
    ///     that is paused and starts every known agent that is not. Solo has no assignment grid, so the
    ///     evaluation that does this in Balanced mode issues nothing here.
    /// </summary>
    internal Task ApplySoloRestrictionsAsync() => startAllAgentsAsync();

    private async Task startAllAgentsAsync()
    {
        await _soloPassLock.WaitAsync(_cancellation.Token);
        try
        {
            await startAllAgentsUnlockedAsync();
        }
        finally
        {
            _soloPassLock.Release();
        }
    }

    private async Task startAllAgentsUnlockedAsync()
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

        // GH-5458: a pause stops the agent, as the assignment grid does in Balanced. Skipping the start alone
        // was not enough: nothing else stops a paused agent in Solo -- ApplyRestrictionsAsync's evaluation has
        // no grid to detach it from -- so a shard paused while running kept advancing indefinitely.
        foreach (var uri in AllRunningAgentUris().Where(paused.Contains))
        {
            try
            {
                await StopAgentAsync(uri);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to stop paused agent {AgentUri}", uri);
            }
        }

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
                // A running paused agent was stopped above (GH-5458).
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