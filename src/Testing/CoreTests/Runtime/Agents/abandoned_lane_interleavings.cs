using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// What AbandonLane guarantees while an Enqueue or a lane worker is running against it, one interleaving per
/// test, pinned through <see cref="AgentCommandDispatcher.Interleave" />.
///
/// <para>Each window here is interior to a single call and a few statements wide — a health check landing
/// between the two halves of an Enqueue, a command finishing between the abandonment latch and the
/// cancellation that follows it — so no sequence of public calls reaches one, and racing the threads against
/// each other does not either. The seam parks one thread at a named point; the test then drives the other
/// through the window and asserts on what it finds.</para>
/// </summary>
public class abandoned_lane_interleavings
{
    private static readonly Guid NodeA = Guid.NewGuid();
    private static readonly Guid NodeB = Guid.NewGuid();

    private static NodeDestination destination(Guid nodeId) => new(nodeId, $"fake://{nodeId}".ToUri());

    private static Uri agent(string name) => new($"fake://{name}");

    private static AgentCommandDispatcher dispatcherFor(
        Func<IAgentCommand, CancellationToken, Task<AgentCommands?>> executor)
        => new(executor, NullLogger.Instance, TestContext.Current.CancellationToken);

    /// <summary>
    /// Parks the first thread to reach one named point until it is released, and says when it got there.
    /// Later arrivals pass straight through, so a parked thread never blocks the one driving it.
    /// </summary>
    private sealed class Parked(string point)
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // An event rather than a second task: this one is waited on synchronously, inside the seam, on
        // whichever thread the production code happens to be running.
        private readonly ManualResetEventSlim _resume = new();
        private int _arrivals;

        public Task Reached => _reached.Task;

        public void Handle(string reached)
        {
            if (reached != point || Interlocked.Increment(ref _arrivals) > 1) return;

            _reached.TrySetResult();

            // Bounded, so a test that mis-sequences fails on its own assertion rather than hanging the runner.
            _resume.Wait(30.Seconds());
        }

        public void Release() => _resume.Set();
    }

    private static Task<AgentCommands?> nothing() => Task.FromResult<AgentCommands?>(AgentCommands.Empty);

    /// <summary>
    /// A dispatch's claims and the queue entry that owns it are published in one order only, because the
    /// entry is what makes the dispatch visible to AbandonLane's scan. Published the other way round, a scan
    /// landing here takes the entry, finds no claims to clear and reports nothing -- and the claims that land
    /// afterwards are then skipped by every later release() on its single-shot gate, stranding the agent for
    /// the life of the process: suppressed from this node by Enqueue's freshness filter and permanently
    /// pending to the leader.
    /// </summary>
    [Fact]
    public async Task abandoning_a_lane_mid_enqueue_strands_no_claim()
    {
        var parked = new Parked(AgentCommandDispatcher.EnqueueQueued);

        await using var dispatcher = dispatcherFor((_, _) => nothing());
        dispatcher.Interleave = parked.Handle;

        var enqueueing = Task.Run(
            () => dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")])),
            TestContext.Current.CancellationToken);

        await parked.Reached.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The health check finds NodeA gone while that enqueue is only half published.
        var released = dispatcher.AbandonLane(NodeA);

        parked.Release();
        await enqueueing.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The agent has to come back to the leader -- the command will never run, its lane is gone.
        released.ShouldBe([(agent("one"), NodeA)]);

        // ...and nothing may be left behind holding it, either.
        dispatcher.InFlightAgents.ShouldBeEmpty();
    }

    /// <summary>
    /// Nothing may cascade out of a lane whose claims have been handed back. Abandonment is latched before
    /// the claims are released and the lane's token is only cancelled after, so a command completing in that
    /// window would otherwise still see a live lane and start its agents on the destination the leader has
    /// since been told is free to re-decide -- with no ledger entry left to suppress the second placement.
    /// </summary>
    [Fact]
    public async Task a_command_finishing_after_its_claims_are_released_cascades_nothing()
    {
        var parked = new Parked(AgentCommandDispatcher.AbandonReleased);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cascades = new ConcurrentQueue<Guid>();

        await using var dispatcher = dispatcherFor(async (command, _) =>
        {
            if (command is AssignAgent assign)
            {
                cascades.Enqueue(assign.Destination.NodeId);
                return AgentCommands.Empty;
            }

            entered.TrySetResult();

            // Finishes only once the abandonment has let the agents go, and before it has cancelled anything.
            await parked.Reached;
            return new AgentCommands { new AssignAgent(agent("one"), destination(NodeB)) };
        });

        dispatcher.Interleave = parked.Handle;

        dispatcher.Enqueue(new ReassignAgents(destination(NodeA), destination(NodeB), [agent("one")]));
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var abandoning = Task.Run(() => dispatcher.AbandonLane(NodeA), TestContext.Current.CancellationToken);
        await parked.Reached.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Generous, deliberately: the command is running to completion in this window and the assertion is
        // that its cascade never escapes, not that it is merely late.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        cascades.ShouldBeEmpty();

        parked.Release();
        (await abandoning.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken))
            .ShouldBe([(agent("one"), NodeB)]);

        cascades.ShouldBeEmpty();
    }

    /// <summary>
    /// Completing a lane's writer leaves what is already buffered readable, so the worker has to re-check
    /// abandonment after each read and not only when its token is cancelled. In this window the token is
    /// still live, and executing the next buffered command sends a real stop or start to a node that has left
    /// the cluster, for agents the leader has just been told are free.
    /// </summary>
    [Fact]
    public async Task buffered_work_is_not_executed_once_the_lane_is_latched()
    {
        var parked = new Parked(AgentCommandDispatcher.AbandonReleased);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<Uri>();

        await using var dispatcher = dispatcherFor(async (command, _) =>
        {
            var first = entered.TrySetResult();
            foreach (var uri in AgentCommandDispatcher.StartedAgentsOf(command)) executed.Enqueue(uri);

            // The first command finishes inside the window; anything after it should never get here.
            if (first) await parked.Reached;
            return AgentCommands.Empty;
        });

        dispatcher.Interleave = parked.Handle;

        // Both for NodeA, so the second sits buffered in the lane behind the first.
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("two")]));

        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var abandoning = Task.Run(() => dispatcher.AbandonLane(NodeA), TestContext.Current.CancellationToken);
        await parked.Reached.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        executed.ShouldBe([agent("one")]);

        parked.Release();
        await abandoning.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        executed.ShouldBe([agent("one")]);
        dispatcher.InFlightAgents.ShouldBeEmpty();
    }

    /// <summary>
    /// Removing a lane from the map does not stop an Enqueue for the same node from landing on a replacement
    /// a moment later, and _queued is global. The scan therefore matches on the lane an enqueue was written
    /// to rather than on the node it names: the replacement's command is live, its own worker is running it,
    /// and reporting its agents to the leader here places a second copy while the first is still starting.
    /// </summary>
    [Fact]
    public async Task a_replacement_lane_created_mid_abandonment_keeps_its_claims()
    {
        var parked = new Parked(AgentCommandDispatcher.AbandonLatched);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var dispatcher = dispatcherFor(async (_, _) =>
        {
            entered.TrySetResult();
            await gate.Task;
            return AgentCommands.Empty;
        });

        dispatcher.Interleave = parked.Handle;

        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("one")]));
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var abandoning = Task.Run(() => dispatcher.AbandonLane(NodeA), TestContext.Current.CancellationToken);
        await parked.Reached.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // An evaluation lands in exactly that window. The old lane is already out of the map, so this gets a
        // replacement -- one the abandonment in flight has no business touching.
        dispatcher.Enqueue(new AssignAgents(destination(NodeA), [agent("two")]));

        parked.Release();

        (await abandoning.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken))
            .ShouldBe([(agent("one"), NodeA)]);

        dispatcher.TryFindPendingDestination(agent("two"), out var held).ShouldBeTrue();
        held.ShouldBe(NodeA);

        gate.SetResult();
    }
}
