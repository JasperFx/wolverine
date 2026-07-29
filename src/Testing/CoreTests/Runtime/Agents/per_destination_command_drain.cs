using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for GH-3698. The leader's agent command drain used to execute commands one at a
/// time across the whole cluster. Combined with <c>NodeAgentController.batchCommands</c>, which groups a
/// wave's AssignAgent commands by destination and appends each destination's chunks contiguously, that
/// made the drain destination-at-a-time: every chunk for node A completed before node B was sent its
/// first. Cluster-wide agent start concurrency was capped at one node's MaxAgentStartParallelism no matter
/// how many nodes existed, so a cluster of thousands of slow-starting subscription agents took hours with
/// one node working and the rest idle — and because executeHealthChecks awaits the drain, the leader also
/// stopped re-evaluating assignments (and writing AssignmentChanged records) for the whole duration.
///
/// <para>The end-to-end proof lives in <c>SlowTests/Agents/agent_assignment_at_scale.cs</c>, which needs a
/// real Postgres store and three hosts. These tests pin the drain's contract directly.</para>
/// </summary>
public class per_destination_command_drain
{
    private static readonly Guid NodeA = Guid.NewGuid();
    private static readonly Guid NodeB = Guid.NewGuid();
    private static readonly Guid NodeC = Guid.NewGuid();

    private static NodeDestination destination(Guid nodeId) => new(nodeId, $"fake://{nodeId}".ToUri());

    /// <summary>
    /// A command that blocks until released, recording when it enters and leaves so a test can assert on
    /// what overlapped with what.
    /// </summary>
    private record GatedCommand(string Name, Guid? DestinationNodeId, Task Gate, ConcurrentQueue<string> Log)
        : IAgentCommand
    {
        public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
        {
            Log.Enqueue($"enter:{Name}");
            await Gate;
            Log.Enqueue($"exit:{Name}");
            return AgentCommands.Empty;
        }
    }

    private static AgentCommandDrain drainFor(Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> executor)
        => new(executor, NullLogger.Instance);

    [Fact]
    public async Task commands_for_different_destinations_run_concurrently()
    {
        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        var commands = new AgentCommands
        {
            new GatedCommand("a", NodeA, gate.Task, log),
            new GatedCommand("b", NodeB, gate.Task, log),
            new GatedCommand("c", NodeC, gate.Task, log)
        };

        var drain = drainFor(async (command, token) =>
        {
            // Every destination must be able to be in flight at the same instant. Before the fix, only one
            // command ever ran at a time and this never reached 3 — the drain would deadlock on the gate.
            if (Interlocked.Increment(ref entered) == 3) allEntered.TrySetResult();
            return await command.ExecuteAsync(null!, token);
        });

        var draining = drain.DrainAsync(commands, CancellationToken.None);

        await allEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        gate.SetResult();
        await draining.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        log.Count(x => x.StartsWith("enter:")).ShouldBe(3);
    }

    [Fact]
    public async Task commands_for_the_same_destination_run_one_at_a_time()
    {
        var log = new ConcurrentQueue<string>();
        var inFlight = 0;
        var peak = 0;

        var commands = new AgentCommands();
        for (var i = 0; i < 5; i++)
        {
            commands.Add(new GatedCommand($"a{i}", NodeA, Task.CompletedTask, log));
        }

        var drain = drainFor(async (command, token) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, current);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            return await command.ExecuteAsync(null!, token);
        });

        await drain.DrainAsync(commands, CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // One chunk in flight per destination at a time is what the WO-5 pending-assignment ledger assumes.
        peak.ShouldBe(1);

        // ...and in the order they were emitted.
        log.Where(x => x.StartsWith("enter:")).ShouldBe(
            ["enter:a0", "enter:a1", "enter:a2", "enter:a3", "enter:a4"]);
    }

    [Fact]
    public async Task commands_without_a_destination_share_one_serial_lane()
    {
        var log = new ConcurrentQueue<string>();
        var inFlight = 0;
        var peak = 0;

        var commands = new AgentCommands
        {
            new GatedCommand("x", null, Task.CompletedTask, log),
            new GatedCommand("y", null, Task.CompletedTask, log),
            new GatedCommand("z", null, Task.CompletedTask, log)
        };

        var drain = drainFor(async (command, token) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, current);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            return await command.ExecuteAsync(null!, token);
        });

        await drain.DrainAsync(commands, CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        peak.ShouldBe(1);
        log.Where(x => x.StartsWith("enter:")).ShouldBe(["enter:x", "enter:y", "enter:z"]);
    }

    [Fact]
    public async Task one_failing_command_does_not_discard_the_rest_of_the_wave()
    {
        var executed = new ConcurrentBag<string>();

        var commands = new AgentCommands
        {
            // Before the fix, this TimeoutException unwound out of the drain and every command still queued
            // for every OTHER node was silently dropped -- one slow node voided a whole assignment wave.
            new GatedCommand("boom", NodeA, Task.FromException(new TimeoutException()), new ConcurrentQueue<string>()),
            new GatedCommand("a-next", NodeA, Task.CompletedTask, new ConcurrentQueue<string>()),
            new GatedCommand("b", NodeB, Task.CompletedTask, new ConcurrentQueue<string>()),
            new GatedCommand("c", NodeC, Task.CompletedTask, new ConcurrentQueue<string>())
        };

        var drain = drainFor(async (command, token) =>
        {
            var result = await command.ExecuteAsync(null!, token);
            executed.Add(((GatedCommand)command).Name);
            return result;
        });

        var failures = await drain.DrainAsync(commands, CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // ...but the caller is still told what went wrong, so an operator-initiated ApplyRestrictionsAsync
        // can surface it instead of silently reporting success.
        failures.ShouldHaveSingleItem().ShouldBeOfType<TimeoutException>();

        // The failing command never completes, but its lane keeps going and the other lanes are untouched.
        executed.OrderBy(x => x).ShouldBe(["a-next", "b", "c"]);
    }

    [Fact]
    public async Task cascaded_commands_run_after_the_command_that_produced_them()
    {
        var order = new ConcurrentQueue<string>();

        var cascade = new AgentCommands
        {
            new GatedCommand("cascaded", NodeB, Task.CompletedTask, order)
        };

        var producer = new CascadingCommand("producer", NodeA, cascade, order);

        var drain = drainFor((command, token) => command.ExecuteAsync(null!, token)!);

        await drain.DrainAsync(new AgentCommands { producer }, CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // A cascade is deferred to the next round rather than appended to the lane that produced it, so it
        // always runs after its producer even though it is keyed onto a different node's lane.
        order.ShouldBe(["enter:producer", "exit:producer", "enter:cascaded", "exit:cascaded"]);
    }

    private record CascadingCommand(string Name, Guid? DestinationNodeId, AgentCommands Cascade,
        ConcurrentQueue<string> Log) : IAgentCommand
    {
        public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
        {
            Log.Enqueue($"enter:{Name}");
            Log.Enqueue($"exit:{Name}");
            return Task.FromResult(Cascade);
        }
    }
}
