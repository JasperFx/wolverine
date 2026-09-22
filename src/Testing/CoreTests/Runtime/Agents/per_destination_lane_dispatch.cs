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

    /// <summary>
    /// A lane belongs to a node, and when that node leaves the cluster everything in the lane is aimed at a
    /// member that no longer exists. The command parked on its reply window is the one that matters: a
    /// reassignment moving agents OFF the departed node holds them against their destination for the whole
    /// window -- over twenty minutes for a chunk of forty -- and the leader treats them as placed for all of
    /// it. Abandoning the lane releases those claims and reports which agents that freed, so the leader can
    /// re-place them at once.
    /// </summary>
    [Fact]
    public async Task abandoning_a_lane_releases_the_claims_of_the_command_it_is_parked_on()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (command, token) =>
        {
            entered.TrySetResult();

            try
            {
                // Stands in for the stop round trip against a node that will never acknowledge it.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                unwound.TrySetResult();
                throw;
            }

            return AgentCommands.Empty;
        });

        // NodeA has died ungracefully; the leader is moving its agents to NodeB.
        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeB), [agent("one"), agent("two")]));

        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        dispatcher.TryFindPendingDestination(agent("one"), out var held).ShouldBeTrue();
        held.ShouldBe(NodeB);

        var released = dispatcher.AbandonLane(NodeA);

        // Reported with the node each claim was held for, not just the agent: that is what lets the leader
        // tell this claim from a newer one it has since armed for the same agent somewhere else.
        released.OrderBy(x => x.Agent.ToString())
            .ShouldBe([(agent("one"), NodeB), (agent("two"), NodeB)]);
        dispatcher.TryFindPendingDestination(agent("one"), out _).ShouldBeFalse();
        dispatcher.TryFindPendingDestination(agent("two"), out _).ShouldBeFalse();

        // And the doomed command lets go rather than holding a connection for the rest of its reply window.
        await unwound.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The roster sweep the leader actually calls. Every lane whose node is absent from the membership it
    /// just read is abandoned in one pass, and only those: a node still registered keeps its claims however
    /// long its dispatch has been running, because a lane abandoned early would race a live command.
    /// </summary>
    [Fact]
    public async Task abandoning_every_lane_outside_the_roster()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeC), [agent("one")]));
        dispatcher.Enqueue(new AssignAgents(destination(NodeB), [agent("two")]));
        dispatcher.Enqueue(new AssignAgents(destination(NodeC), [agent("three")]));

        // NodeA and NodeB have gone; NodeC is still a member.
        var released = dispatcher.AbandonLanesExcept(new HashSet<Guid> { NodeC }, 1);

        released.OrderBy(x => x.Agent.ToString())
            .ShouldBe([(agent("one"), NodeC), (agent("two"), NodeB)]);
        dispatcher.TryFindPendingDestination(agent("three"), out var kept).ShouldBeTrue();
        kept.ShouldBe(NodeC);

        gate.SetResult();
    }

    /// <summary>
    /// A lagging or half-written snapshot can leave a live node out of one reading. Abandoning its lane
    /// cancels the command running in it -- an AssignAgents mid-start, with some of its agents already
    /// running on a node the leader is about to place them away from, and no stop for those copies -- so
    /// like the ejection of a stale node row, it takes a sustained absence rather than a single reading.
    /// </summary>
    [Fact]
    public async Task a_node_missing_from_a_single_roster_keeps_its_claims()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));

        var roster = new HashSet<Guid> { NodeB };

        dispatcher.AbandonLanesExcept(roster, 2).ShouldBeEmpty();
        dispatcher.TryFindPendingDestination(agent("one"), out var held).ShouldBeTrue();
        held.ShouldBe(NodeA);

        // Absent again on the next sweep, so the node really is gone.
        dispatcher.AbandonLanesExcept(roster, 2).ShouldBe([(agent("one"), NodeA)]);

        gate.SetResult();
    }

    /// <summary>
    /// ...and a node that is back in the roster starts its absence over, so a node that blips out every
    /// other tick is never abandoned.
    /// </summary>
    [Fact]
    public async Task a_node_that_reappears_starts_its_absence_over()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));

        dispatcher.AbandonLanesExcept(new HashSet<Guid> { NodeB }, 2).ShouldBeEmpty();
        dispatcher.AbandonLanesExcept(new HashSet<Guid> { NodeA, NodeB }, 2).ShouldBeEmpty();
        dispatcher.AbandonLanesExcept(new HashSet<Guid> { NodeB }, 2).ShouldBeEmpty();

        dispatcher.TryFindPendingDestination(agent("one"), out var held).ShouldBeTrue();
        held.ShouldBe(NodeA);

        gate.SetResult();
    }

    /// <summary>
    /// Abandoning one node's lane must not disturb another's -- the claims released are only the ones the
    /// departed node's own commands were holding.
    /// </summary>
    [Fact]
    public async Task abandoning_a_lane_leaves_other_lanes_alone()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeC), [agent("one")]));
        dispatcher.Enqueue(new AssignAgents(destination(NodeB), [agent("two")]));

        dispatcher.AbandonLane(NodeA).ShouldBe([(agent("one"), NodeC)]);

        dispatcher.TryFindPendingDestination(agent("one"), out _).ShouldBeFalse();
        dispatcher.TryFindPendingDestination(agent("two"), out var stillHeld).ShouldBeTrue();
        stillHeld.ShouldBe(NodeB);

        gate.SetResult();
    }

    /// <summary>
    /// A claim a re-target has taken over belongs to a dispatch that is still live, so the ejected node's
    /// command must not report it. The leader deletes a reported agent's ledger entry outright, and would
    /// place the agent a second time while the re-target is still starting the first copy.
    /// </summary>
    [Fact]
    public async Task abandoning_a_lane_does_not_report_a_claim_a_retarget_has_taken_over()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            await gate.Task;
            return AgentCommands.Empty;
        });

        // Queued against the node that is about to be ejected...
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one"), agent("two")]));

        // ...and then one of the two re-targeted to a live node, which takes that agent's claim.
        dispatcher.Enqueue(new AssignAgents(destination(NodeC), [agent("one")]));

        dispatcher.TryFindPendingDestination(agent("one"), out var retargeted).ShouldBeTrue();
        retargeted.ShouldBe(NodeC);

        // Only "two" is still NodeA's to let go of.
        dispatcher.AbandonLane(NodeA).ShouldBe([(agent("two"), NodeA)]);

        dispatcher.TryFindPendingDestination(agent("one"), out var stillHeld).ShouldBeTrue();
        stillHeld.ShouldBe(NodeC);

        gate.SetResult();
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
    /// off a node that had just gone stale.
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

        // The re-target went to a different lane and is running there, while the doomed copy sits on NodeA.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        destinations.Distinct().OrderBy(x => x).Count().ShouldBe(2);

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
    /// The grace period first, the cancellation after. A timed-out lane used to be walked away from but
    /// left running with a live token, holding whatever it was parked on for the rest of the process.
    /// </summary>
    [Fact]
    public async Task disposal_cancels_a_lane_that_outran_the_shutdown_budget()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = dispatcherFor(async (_, token) =>
        {
            running.TrySetResult();

            try
            {
                // Stands in for a reply window this lane will not see the end of.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));
        await running.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Still inside its grace period, so nothing has interrupted it.
        cancelled.Task.IsCompleted.ShouldBeFalse();

        await withLaneShutdownTimeout(500.Milliseconds(),
            async () => await dispatcher.DisposeAsync().AsTask()
                .WaitAsync(10.Seconds(), TestContext.Current.CancellationToken));

        await cancelled.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
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
    /// Nothing may cascade out of a lane that has been abandoned. The stop half of a ReassignAgents aimed at
    /// a departed node can still complete -- or swallow its cancellation -- after AbandonLane has let the
    /// agents go, and its cascade would then start them on the destination the leader has since re-decided,
    /// with no ledger entry left to suppress the duplicate. Abandonment is therefore latched before the
    /// claims are released, and tested under the same gate the cascade is enqueued under, so a completing
    /// command either cascades entirely before the lane is abandoned or not at all.
    /// </summary>
    [Fact]
    public async Task a_cascade_out_of_an_abandoned_lane_is_dropped()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cascades = new ConcurrentQueue<Guid>();

        await using var dispatcher = dispatcherFor(async (command, _) =>
        {
            if (command is AssignAgent assign)
            {
                cascades.Enqueue(assign.Destination.NodeId);
                return AgentCommands.Empty;
            }

            entered.TrySetResult();

            // Deliberately not token-aware: the shape of a stop that sits out its reply window against a
            // node that is gone and then returns normally anyway.
            await gate.Task;
            return new AgentCommands { new AssignAgent(agent("one"), destination(NodeB)) };
        });

        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeB), [agent("one")]));
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        dispatcher.AbandonLane(NodeA).ShouldBe([(agent("one"), NodeB)]);

        // Generous, deliberately: the assertion is that the cascade never runs, not that it is merely late.
        gate.SetResult();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        cascades.ShouldBeEmpty();

        // ...and the agent is left free rather than re-claimed, so the next evaluation places it wherever
        // it decides.
        dispatcher.TryFindPendingDestination(agent("one"), out _).ShouldBeFalse();
    }

    /// <summary>
    /// A command's release has to belong to the enqueue that owns it, not to anything merely equal to it. An
    /// abandoned lane releases its commands there and then, while the worker is still parked on one of them,
    /// so that worker's own release lands later -- by which time the node can be back in the roster and the
    /// identical batch legitimately queued onto a fresh lane. Keyed on command equality alone, the stale
    /// release stripped the live dispatch's claim, and the leader would place the agent a second time.
    /// </summary>
    [Fact]
    public async Task a_stale_release_does_not_clear_an_equal_command_queued_since()
    {
        var entered = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        var gates = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        var calls = 0;

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            entered[index].TrySetResult();
            await gates[index].Task;
            return AgentCommands.Empty;
        });

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));
        await entered[0].Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // NodeA is missing for long enough to be abandoned, so the claim goes while the worker is parked.
        dispatcher.AbandonLane(NodeA).ShouldBe([(agent("one"), NodeA)]);

        // ...and then NodeA is back, and the next evaluation issues the identical batch onto a fresh lane.
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));
        await entered[1].Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The abandoned worker finally unwinds. Generous, deliberately: the assertion is that its release
        // never touches the live claim, not that it is merely slow to.
        gates[0].SetResult();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        dispatcher.TryFindPendingDestination(agent("one"), out var held).ShouldBeTrue();
        held.ShouldBe(NodeA);

        gates[1].SetResult();
    }
}
