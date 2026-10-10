using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3698. The leader's agent commands execute on a persistent lane per destination node. Two earlier
/// shapes each failed the same way in a different place:
///
/// <list type="number">
/// <item>Strictly serial across the cluster — one node slow to start its share held up every other node, so
/// cluster-wide start concurrency was capped at one node's MaxAgentStartParallelism regardless of node
/// count.</item>
/// <item>Laned per <i>wave</i> but one wave at a time — a wave holding an AssignAgents aimed at a node that
/// had just been removed blocked on its 30s+chunkSize reply window while the re-targets to the surviving
/// nodes queued behind it. That regressed seven RavenDb leadership-takeover tests, which is what these
/// tests exist to prevent recurring.</item>
/// </list>
/// </summary>
public class per_destination_lane_dispatch
{
    private static readonly Guid NodeA = Guid.NewGuid();
    private static readonly Guid NodeB = Guid.NewGuid();
    private static readonly Guid NodeC = Guid.NewGuid();

    private static NodeDestination destination(Guid nodeId) => new(nodeId, $"fake://{nodeId}".ToUri());

    private static Uri agent(string name) => new($"fake://{name}");

    private record GatedCommand(string Name, Guid? DestinationNodeId, Task Gate, ConcurrentQueue<string> Log)
        : IAgentCommand
    {
        public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken token)
        {
            Log.Enqueue($"enter:{Name}");
            await Gate;
            Log.Enqueue($"exit:{Name}");
            return AgentCommands.Empty;
        }
    }

    private static AgentCommandDispatcher dispatcherFor(
        Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> executor)
        => new(executor, NullLogger.Instance, TestContext.Current.CancellationToken);

    private static Task<AgentCommands?> execute(IAgentCommand command, CancellationToken token)
        => command.ExecuteAsync(null!, token)!;

    /// <summary>
    /// The regression that broke the RavenDb leadership-takeover suite: a lane wedged on a node that no
    /// longer answers must not stop a different node's work from running.
    /// </summary>
    [Fact]
    public async Task a_wedged_lane_does_not_block_another_destination()
    {
        var log = new ConcurrentQueue<string>();
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            var result = await execute(command, token);
            if (((GatedCommand)command).Name == "healthy") healthyRan.TrySetResult();
            return result;
        });

        // Stands in for an AssignAgents whose destination was just ejected: it will sit on its reply window.
        dispatcher.Enqueue(new GatedCommand("wedged", NodeA, wedged.Task, log));
        dispatcher.Enqueue(new GatedCommand("healthy", NodeB, Task.CompletedTask, log));

        // The healthy node's command completes while the wedged one is still blocked.
        await healthyRan.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        log.ShouldContain("exit:healthy");
        log.ShouldNotContain("exit:wedged");

        wedged.SetResult();
    }

    /// <summary>
    /// The same isolation across successive waves — the specific head-of-line blocking that the one-wave-at-
    /// a-time pump had. Work queued AFTER a lane wedges must still run on other lanes.
    /// </summary>
    [Fact]
    public async Task work_queued_after_a_lane_wedges_still_runs_on_other_lanes()
    {
        var log = new ConcurrentQueue<string>();
        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            var result = await execute(command, token);
            if (((GatedCommand)command).Name == "later") laterRan.TrySetResult();
            return result;
        });

        dispatcher.Enqueue(new GatedCommand("wedged", NodeA, wedged.Task, log));

        // Simulate the next evaluation landing while the first lane is still stuck.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        dispatcher.Enqueue(new GatedCommand("later", NodeC, Task.CompletedTask, log));

        await laterRan.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        log.ShouldNotContain("exit:wedged");

        wedged.SetResult();
    }

    [Fact]
    public async Task commands_for_the_same_destination_run_one_at_a_time_in_order()
    {
        var log = new ConcurrentQueue<string>();
        var inFlight = 0;
        var peak = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, current);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            var result = await execute(command, token);
            if (Interlocked.Increment(ref completed) == 5) done.TrySetResult();
            return result;
        });

        for (var i = 0; i < 5; i++)
        {
            dispatcher.Enqueue(new GatedCommand($"a{i}", NodeA, Task.CompletedTask, log));
        }

        await done.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // One command in flight per destination at a time is what the pending-assignment ledger assumes.
        peak.ShouldBe(1);
        log.Where(x => x.StartsWith("enter:")).ShouldBe(
            ["enter:a0", "enter:a1", "enter:a2", "enter:a3", "enter:a4"]);
    }

    [Fact]
    public async Task an_identical_command_already_queued_is_not_queued_again()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = 0;

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            Interlocked.Increment(ref executed);
            return await execute(command, token);
        });

        var command = new AssignAgents(destination(NodeA), [agent("one"), agent("two")]);

        dispatcher.Enqueue(command);
        // Same agents, different order, fresh array — equal by value, so it must collapse.
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("two"), agent("one")]));

        dispatcher.InFlightAgents.Count.ShouldBe(2);
        gate.SetResult();
    }

    [Fact]
    public async Task a_batch_is_narrowed_to_the_agents_not_already_in_flight()
    {
        var seen = new ConcurrentBag<Uri[]>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            if (command is AssignAgents batch) seen.Add(batch.AgentIds);
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one"), agent("two")]));

        // The next evaluation re-chunks the remainder differently -- three agents, two of them already in
        // flight. Only the genuinely new one may be queued; no command-level comparison could see this.
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one"), agent("two"), agent("three")]));

        dispatcher.InFlightAgents.Keys.OrderBy(x => x.ToString())
            .ShouldBe([agent("one"), agent("three"), agent("two")]);

        gate.SetResult();
    }

    /// <summary>
    /// A re-target is not a duplicate. Keying in-flight suppression on the agent alone stranded an agent for
    /// as long as the doomed in-flight copy took to time out — exactly when the leader was trying to move it
    /// off a node that had just gone stale. The doomed copy itself is skipped if its lane has not taken it up
    /// yet (GH-4901 follow-up: a start a newer one has superseded is never claimed), and runs out its own
    /// reply window if it has; either way it holds nothing up.
    /// </summary>
    [Fact]
    public async Task the_same_agent_aimed_at_a_different_node_is_not_suppressed()
    {
        var destinations = new ConcurrentBag<Guid?>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            destinations.Add(command.DestinationNodeId);
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgent(agent("one"), destination(NodeA)));
        dispatcher.Enqueue(new AssignAgent(agent("one"), destination(NodeB)));

        // The re-target went to a different lane and is running there, whatever became of the doomed copy.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        destinations.ShouldContain(NodeB);

        gate.SetResult();
    }

    [Fact]
    public async Task commands_without_a_destination_share_one_serial_lane()
    {
        var log = new ConcurrentQueue<string>();
        var inFlight = 0;
        var peak = 0;
        var completed = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, current);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            var result = await execute(command, token);
            if (Interlocked.Increment(ref completed) == 3) done.TrySetResult();
            return result;
        });

        dispatcher.Enqueue(new GatedCommand("x", null, Task.CompletedTask, log));
        dispatcher.Enqueue(new GatedCommand("y", null, Task.CompletedTask, log));
        dispatcher.Enqueue(new GatedCommand("z", null, Task.CompletedTask, log));

        await done.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        peak.ShouldBe(1);
        dispatcher.LaneCount.ShouldBe(1);
        log.Where(x => x.StartsWith("enter:")).ShouldBe(["enter:x", "enter:y", "enter:z"]);
    }

    [Fact]
    public async Task a_failing_command_releases_its_claims_so_the_work_can_be_retried()
    {
        var attempts = 0;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor((command, token) =>
        {
            Interlocked.Increment(ref attempts);
            failed.TrySetResult();
            throw new TimeoutException();
        });

        var command = new AssignAgent(agent("one"), destination(NodeA));
        dispatcher.Enqueue(command);

        await failed.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The claim must be gone once the command finished, however it finished, or the pending-assignment
        // ledger's retry after its TTL would be silently dropped forever.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        dispatcher.InFlightAgents.ShouldBeEmpty();

        dispatcher.Enqueue(new AssignAgent(agent("one"), destination(NodeA)));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        attempts.ShouldBe(2);
    }

    /// <summary>
    /// GH-3781. Completing a channel writer does not throw away what is already buffered, so the old
    /// DisposeAsync executed every command still queued for a node the cluster was leaving -- each costing
    /// its own AgentBatchTimeouts reply window (25.5 minutes at AgentStartBatchSize = 50) inside
    /// IHost.StopAsync.
    /// </summary>
    [Fact]
    public async Task disposal_abandons_the_commands_still_queued()
    {
        var log = new ConcurrentQueue<string>();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = dispatcherFor(async (command, token) =>
        {
            running.TrySetResult();
            return await execute(command, token);
        });

        // Both aimed at the same node, so the second sits in the lane behind the first.
        dispatcher.Enqueue(new GatedCommand("first", NodeA, gate.Task, log));
        dispatcher.Enqueue(new GatedCommand("second", NodeA, Task.CompletedTask, log));

        await running.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // DisposeAsync latches and empties the lane queues synchronously, before its first await, so
        // releasing the gate afterwards is deterministic rather than a race. Ordered this way the
        // unfixed code fails this test on the assertion below instead of deadlocking the runner --
        // which matters for a regression test whose subject is a shutdown that never returns.
        var disposal = withLaneShutdownTimeout(500.Milliseconds(),
            async () => await dispatcher.DisposeAsync());
        gate.SetResult();
        await disposal.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Generous, deliberately: the point is that "second" never runs, not that it is merely late.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        log.ShouldContain("exit:first");
        log.ShouldNotContain("enter:second");

        // And nothing is left claimed, or a later leader would suppress re-issuing this work.
        dispatcher.InFlightAgents.ShouldBeEmpty();
    }

    /// <summary>
    /// GH-3781, the backstop. The wedge was one lane parked on a reply from a node that had already gone,
    /// with teardownAgentsAsync -- and so the node's own deregistration -- queued behind it. Disposal has to
    /// give up on a lane rather than hold IHost.StopAsync() with it, however that lane came to be stuck.
    /// </summary>
    [Fact]
    public async Task disposal_gives_up_on_a_lane_that_ignores_cancellation()
    {
        var log = new ConcurrentQueue<string>();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = dispatcherFor(async (command, token) =>
        {
            running.TrySetResult();
            // Deliberately not token-aware -- this is the shape of an InvokeAsync sitting out a reply window.
            await never.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new GatedCommand("wedged", NodeA, Task.CompletedTask, log));
        await running.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();

        // The outer WaitAsync is what keeps the UNFIXED code from wedging this runner the way it wedges
        // IHost.StopAsync -- it turns the defect into a failed assertion instead of a hung CI job.
        await withLaneShutdownTimeout(500.Milliseconds(),
            async () => await dispatcher.DisposeAsync().AsTask()
                .WaitAsync(10.Seconds(), TestContext.Current.CancellationToken));
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(5.Seconds());

        never.SetResult();
    }

    /// <summary>
    /// PR #4537's finding. A reassignment runs in its SOURCE's lane and waits for the stop to be confirmed; a
    /// node that was killed confirms nothing, so the lane sat out the whole reply window while the dispatcher
    /// reported the agents as still moving and the leader's ledger held them there. Once the node is known to
    /// have left, its lane is abandoned: the executing command is cancelled, what was queued behind it is
    /// dropped, every claim is released, and a node returning under the same id gets a fresh lane.
    /// </summary>
    [Fact]
    public async Task abandoning_a_departed_nodes_lane_cancels_its_work_and_frees_its_agents()
    {
        var log = new ConcurrentQueue<string>();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;

        static string name(IAgentCommand command) => command switch
        {
            ReassignAgents move => $"move:{move.AgentUris[0]}",
            AssignAgents start => $"start:{start.AgentIds[0]}",
            _ => command.GetType().Name
        };

        var dispatcher = dispatcherFor(async (command, token) =>
        {
            log.Enqueue($"enter:{name(command)}");

            // The first command into node A's lane is a stop against a node that will never answer. It honours
            // the token, as AgentWorkConfirmation's polling does.
            if (command.DestinationNodeId == NodeA && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                running.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }

            log.Enqueue($"exit:{name(command)}");
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeB), [agent("a1"), agent("a2")]));
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("a3")]));
        await running.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        dispatcher.TryFindPendingDestination(agent("a1"), out var movingTo).ShouldBeTrue();
        movingTo.ShouldBe(NodeB);

        dispatcher.AbandonLane(NodeA);
        await cancelled.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The lane lets go of everything it held, so the leader is free to re-place these agents
        await waitUntilAsync(() => !dispatcher.TryFindPendingDestination(agent("a1"), out _));
        dispatcher.TryFindPendingDestination(agent("a2"), out _).ShouldBeFalse();
        dispatcher.TryFindPendingDestination(agent("a3"), out _).ShouldBeFalse();
        dispatcher.InFlightAgents.ShouldBeEmpty();
        dispatcher.LaneCount.ShouldBe(0);

        // What was queued behind the cancelled command never runs
        await Task.Delay(200, TestContext.Current.CancellationToken);
        log.ShouldNotContain($"enter:start:{agent("a3")}");

        // The node comes back under the same id: a fresh lane works its queue
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("a4")]));
        await waitUntilAsync(() => log.Contains($"exit:start:{agent("a4")}"));
        dispatcher.LaneCount.ShouldBe(1);
    }

    private static async Task waitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static async Task withLaneShutdownTimeout(TimeSpan timeout, Func<Task> action)
    {
        var previous = AgentCommandDispatcher.LaneShutdownTimeout;
        AgentCommandDispatcher.LaneShutdownTimeout = timeout;
        try
        {
            await action();
        }
        finally
        {
            AgentCommandDispatcher.LaneShutdownTimeout = previous;
        }
    }

    [Fact]
    public async Task a_cascade_is_routed_to_the_lane_of_the_node_it_targets()
    {
        var order = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor((command, token) =>
        {
            if (command is AssignAgent assign)
            {
                order.Enqueue($"assign:{assign.Destination.NodeId}");
                done.TrySetResult();
                return Task.FromResult<AgentCommands?>(AgentCommands.Empty);
            }

            order.Enqueue("reassign");
            return Task.FromResult<AgentCommands?>(
                [new AssignAgent(agent("one"), destination(NodeB))]);
        });

        dispatcher.Enqueue(new ReassignAgent(agent("one"), destination(NodeA), destination(NodeB)));

        await done.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        order.ToList().ShouldBe(["reassign", $"assign:{NodeB}"]);
    }

    /// <summary>
    /// GH-4901. A reassignment runs in its source node's lane, behind whatever that node still has queued. For
    /// an agent whose start is itself still in that queue there is nothing to stop yet, so the move must not
    /// wait: in the field the second and third green nodes of a warm-up got nothing for half an hour while the
    /// first worked through every green-only start queued for it before they registered.
    /// </summary>
    [Fact]
    public async Task a_move_of_a_start_still_queued_goes_straight_to_the_new_node()
    {
        var executed = new ConcurrentQueue<IAgentCommand>();
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retargeted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            executed.Enqueue(command);
            switch (command)
            {
                case AssignAgent { AgentUri.Host: "busy" }:
                    await busy.Task;
                    break;
                case AssignAgents batch when batch.Destination.NodeId == NodeB:
                    retargeted.TrySetResult();
                    await busy.Task;
                    break;
                case AssignAgent { AgentUri.Host: "last" }:
                    drained.TrySetResult();
                    break;
            }

            return AgentCommands.Empty;
        });

        // Node A is busy starting something, with a batch queued behind it.
        dispatcher.Enqueue(new AssignAgent(agent("busy"), destination(NodeA)));
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one"), agent("two")]));

        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeB), [agent("one"), agent("two")]));

        // Node B gets the starts while node A is still busy, and the leader sees them pending on node B.
        await retargeted.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        dispatcher.TryFindPendingDestination(agent("one"), out var pendingOn).ShouldBeTrue();
        pendingOn.ShouldBe(NodeB);

        // Once node A gets to its queue it skips what was taken off it, and there is nothing left to stop.
        dispatcher.Enqueue(new AssignAgent(agent("last"), destination(NodeA)));
        busy.SetResult();
        await drained.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        executed.OfType<AssignAgents>().Where(x => x.Destination.NodeId == NodeA).ShouldBeEmpty();
        executed.OfType<ReassignAgents>().ShouldBeEmpty();
        executed.OfType<AssignAgents>().ShouldHaveSingleItem().AgentIds.ShouldBe([agent("one"), agent("two")]);
    }

    /// <summary>
    /// GH-4901's boundary: once the source has begun starting an agent, a move is a stop-then-start again and
    /// still queues behind that start (GH-3698), so the stop finds the agent it has to stop.
    /// </summary>
    [Fact]
    public async Task a_move_of_a_start_already_under_way_still_waits_behind_it()
    {
        var executed = new ConcurrentQueue<IAgentCommand>();
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            executed.Enqueue(command);
            switch (command)
            {
                case AssignAgents batch when batch.Destination.NodeId == NodeA:
                    starting.TrySetResult();
                    await started.Task;
                    break;
                case ReassignAgent:
                    moved.TrySetResult();
                    break;
            }

            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one"), agent("two")]));
        await starting.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        dispatcher.Enqueue(new ReassignAgent(agent("one"), destination(NodeA), destination(NodeB)));

        await Task.Delay(100, TestContext.Current.CancellationToken);
        executed.Count.ShouldBe(1, "the move waits for the start it has to stop");

        started.SetResult();
        await moved.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        executed.OfType<ReassignAgent>().ShouldHaveSingleItem().DestinationNodeId.ShouldBe(NodeA);
    }

    /// <summary>
    /// GH-4901, found by the laned chaos soak. A start queued for a node that has since gone reaches its lane after
    /// the leader has already re-started the agent elsewhere. It must not claim the agent: claiming it clobbered the
    /// live start's claim and releasing it afterwards removed that claim, so the next move of the agent withdrew
    /// the live start as "only queued" and started a second copy while the first was still coming up.
    /// </summary>
    [Fact]
    public async Task a_stale_queued_start_does_not_steal_the_claim_of_a_newer_start_elsewhere()
    {
        var executed = new ConcurrentQueue<IAgentCommand>();
        var holdA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startingOnB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneADrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            executed.Enqueue(command);
            switch (command)
            {
                case AssignAgent { AgentUri.Host: "busy" }:
                    await holdA.Task;
                    break;
                case AssignAgents batch when batch.Destination.NodeId == NodeB:
                    startingOnB.TrySetResult();
                    await holdB.Task;
                    break;
                case AssignAgent { AgentUri.Host: "last" }:
                    laneADrained.TrySetResult();
                    break;
                case ReassignAgent:
                    moved.TrySetResult();
                    break;
            }

            return AgentCommands.Empty;
        });

        // Node A is busy, with a start for one and two queued behind it.
        dispatcher.Enqueue(new AssignAgent(agent("busy"), destination(NodeA)));
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one"), agent("two")]));

        // Node A leaves the cluster and the leader re-targets both to node B, which begins starting them.
        dispatcher.Enqueue(new AssignAgents(destination(NodeB), [agent("one"), agent("two")]));
        await startingOnB.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Now the stale start for node A reaches its lane. It owns nothing any more and is skipped outright.
        dispatcher.Enqueue(new AssignAgent(agent("last"), destination(NodeA)));
        holdA.SetResult();
        await laneADrained.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        executed.OfType<AssignAgents>().Where(x => x.Destination.NodeId == NodeA).ShouldBeEmpty("the stale start was skipped");

        // While node B is still starting one, the leader moves it to C. B's start is under way, so this is a
        // stop-then-start behind it in B's lane -- never a bare start on C.
        dispatcher.Enqueue(new ReassignAgent(agent("one"), destination(NodeB), destination(NodeC)));

        // The leader asks about the move it just issued, and the move -- not the start it supersedes -- is
        // what is outstanding for one now.
        dispatcher.TryFindPendingDestination(agent("one"), out var pendingOn).ShouldBeTrue();
        pendingOn.ShouldBe(NodeC);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        executed.Where(x => x.DestinationNodeId == NodeC).ShouldBeEmpty("one was started on C while B was still starting it");

        holdB.SetResult();
        await moved.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        executed.OfType<ReassignAgent>().ShouldHaveSingleItem().DestinationNodeId.ShouldBe(NodeB);
    }

    /// <summary>
    /// GH-4901, found by the laned chaos soak. The leader re-drives a start whose assignment row has not surfaced
    /// yet; the agent is very likely already running on that node. A move queued behind that re-drive must stop
    /// the agent at the source, not withdraw the re-drive as a start that never ran anywhere.
    /// </summary>
    [Fact]
    public async Task a_re_driven_start_is_not_withdrawn_by_a_later_move()
    {
        var executed = new ConcurrentQueue<IAgentCommand>();
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            executed.Enqueue(command);
            switch (command)
            {
                case AssignAgent { AgentUri.Host: "marker" }:
                    // The lane is serial, so reaching this means the start of one before it has fully finished
                    firstDone.TrySetResult();
                    break;
                case AssignAgent { AgentUri.Host: "busy" }:
                    await holdA.Task;
                    break;
                case ReassignAgent:
                    moved.TrySetResult();
                    break;
            }

            return AgentCommands.Empty;
        });

        // one starts on node A and the lane moves on
        dispatcher.Enqueue(new AssignAgent(agent("one"), destination(NodeA)));
        dispatcher.Enqueue(new AssignAgent(agent("marker"), destination(NodeA)));
        await firstDone.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Node A gets busy, and the leader re-drives one behind it: its row has not turned up yet
        dispatcher.Enqueue(new AssignAgent(agent("busy"), destination(NodeA)));
        dispatcher.Enqueue(new AssignAgent(agent("one"), destination(NodeA)));

        // Then the leader moves one to B. The re-drive is only queued, but one has run on A already.
        dispatcher.Enqueue(new ReassignAgent(agent("one"), destination(NodeA), destination(NodeB)));

        await Task.Delay(100, TestContext.Current.CancellationToken);
        executed.Where(x => x.DestinationNodeId == NodeB).ShouldBeEmpty("one was started on B while A may still be running it");

        holdA.SetResult();
        await moved.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        executed.OfType<ReassignAgent>().ShouldHaveSingleItem().DestinationNodeId.ShouldBe(NodeA);
    }
}
