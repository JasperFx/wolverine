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
}
