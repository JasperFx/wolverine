using JasperFx;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4240: an event-subscription agent ends up running on two nodes at once while the durable
/// assignment record credits only one of them, so nothing in the system can ever stop the extra copy.
///
/// <para>The window is inside <see cref="NodeAgentController.StopAgentAsync" />. It awaits
/// <c>agent.StopAsync()</c> BEFORE taking the agent out of <see cref="NodeAgentController.Agents" />
/// and before dropping the assignment row, and a real shard flips to
/// <see cref="AgentStatus.Stopped" /> early in that teardown. So for the whole duration of the
/// teardown the agent is simultaneously still registered, Stopped, and reporting no failure — which
/// is exactly the state <c>ReportFailedLocalAgentsAsync</c> treats as a wedged shard to be restarted
/// (GH-4193). That sweep runs on EVERY node on EVERY health-check tick independently of leadership,
/// which is why the restart never appears in the leader's own command log: the leader does not issue
/// it, the stopping node issues it to itself.</para>
///
/// <para>The GH-4193 code carries a comment asserting this state is unreachable — "StopAgentAsync
/// removes it from the dictionary and drops the assignment row, so Stopped-and-still-registered is BY
/// CONSTRUCTION a wedged shard and never a deliberate stop". That holds only once
/// <c>StopAgentAsync</c> has RETURNED. These tests pin the behaviour during the await.</para>
/// </summary>
public class deliberate_stop_is_not_swept_up_as_a_wedged_shard
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly CancellationTokenSource _cancellation = new();

    public deliberate_stop_is_not_swept_up_as_a_wedged_shard()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.CheckAssignmentPeriod = 1.Hours();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
    }

    private static readonly Uri TheAgent = new("event-subscriptions://marten/trip/all");

    private NodeAgentController controllerFor(SlowStoppingAgent agent, INodeAgentPersistence? persistence = null)
    {
        var family = new SlowStoppingAgentFamily(TheAgent.Scheme, agent);
        return new NodeAgentController(_runtime, persistence ?? Substitute.For<INodeAgentPersistence>(), [family],
            NullLogger<NodeAgentController>.Instance, _cancellation.Token);
    }

    [Fact]
    public async Task the_sweep_does_not_restart_an_agent_that_is_mid_stop()
    {
        var agent = new SlowStoppingAgent(TheAgent);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(TheAgent);
        agent.StartCount.ShouldBe(1);

        agent.HoldNextStop();

        // Deliberate stop, held open inside agent.StopAsync() the way a Marten shard's teardown holds it
        // open for hundreds of milliseconds.
        var stopping = controller.StopAgentAsync(TheAgent);
        await agent.EnteredStop;

        // The window this test exists for: the agent reports Stopped, reports no failure, and is STILL
        // registered, because StopAgentAsync has not reached its TryRemove yet.
        agent.Status.ShouldBe(AgentStatus.Stopped);
        agent.Failure.ShouldBeNull();
        controller.Agents.ContainsKey(TheAgent).ShouldBeTrue();

        // A health-check tick lands here. It must recognise a stop in progress rather than a wedged shard.
        await controller.ReportFailedLocalAgentsAsync();

        agent.StartCount.ShouldBe(1);

        agent.ReleaseStop();
        await stopping;

        controller.Agents.ContainsKey(TheAgent).ShouldBeFalse();
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task a_restart_during_the_stop_does_not_leave_the_assignment_row_behind()
    {
        // The other half of the damage. StartAgentAsync re-upserts the assignment row (GH-3604 D4), so a
        // restart inside the window re-asserts ownership that the stop is about to revoke — and the stop's
        // RemoveAssignmentAsync then lands on a row belonging to an agent that is once again running.
        var persistence = Substitute.For<INodeAgentPersistence>();
        var agent = new SlowStoppingAgent(TheAgent);
        var controller = controllerFor(agent, persistence);

        await controller.StartAgentAsync(TheAgent);
        persistence.ClearReceivedCalls();

        agent.HoldNextStop();

        var stopping = controller.StopAgentAsync(TheAgent);
        await agent.EnteredStop;

        await controller.ReportFailedLocalAgentsAsync();

        agent.ReleaseStop();
        await stopping;

        // Nothing may have re-claimed this agent for this node while it was being stopped.
        await persistence.DidNotReceive()
            .AddAssignmentAsync(Arg.Any<Guid>(), TheAgent, Arg.Any<CancellationToken>());
        await persistence.Received(1)
            .RemoveAssignmentAsync(Arg.Any<Guid>(), TheAgent, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_genuinely_wedged_shard_is_still_restarted()
    {
        // The guard must not cost GH-4193 its reason to exist: an agent that went Stopped on its own,
        // with no stop command anywhere near it, is still swept back up.
        var agent = new SlowStoppingAgent(TheAgent);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(TheAgent);

        agent.SimulateWedged();

        await controller.ReportFailedLocalAgentsAsync();

        agent.StartCount.ShouldBe(2);
    }

    private class SlowStoppingAgentFamily : IAgentFamily
    {
        private readonly SlowStoppingAgent _agent;

        public SlowStoppingAgentFamily(string scheme, SlowStoppingAgent agent)
        {
            Scheme = scheme;
            _agent = agent;
        }

        public string Scheme { get; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>([_agent.Uri]);

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => ValueTask.FromResult<IAgent>(_agent);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>([_agent.Uri]);

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// An event-subscription agent whose StopAsync is held open, standing in for a Marten shard's
    /// teardown. Status flips to Stopped on the way IN, which is what the real daemon does and what
    /// makes the agent look wedged to the sweep.
    /// </summary>
    private class SlowStoppingAgent : IEventSubscriptionAgent
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowStoppingAgent(Uri uri) => Uri = uri;

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
        public ShardFailure? Failure => null;

        private int _stopCount;
        private int _holdNextStop;

        public int StartCount { get; private set; }
        public int StopCount => Volatile.Read(ref _stopCount);

        public Task EnteredStop => _entered.Task;

        /// <summary>Hold the next StopAsync open, standing in for a Marten shard's slow teardown.</summary>
        public void HoldNextStop() => Interlocked.Exchange(ref _holdNextStop, 1);

        public void ReleaseStop() => _release.TrySetResult();

        /// <summary>The GH-4193 case: the shard died underneath us with no stop command in sight.</summary>
        public void SimulateWedged() => Status = AgentStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            Status = AgentStatus.Running;
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopCount);

            // A real shard reports Stopped from the moment teardown begins, not when it finishes.
            Status = AgentStatus.Stopped;

            // Only a stop the test has explicitly armed is held open, and only once. GH-3519's wedge
            // recovery calls StopAsync again on its way to restarting the agent; holding that one too
            // would deadlock the test instead of letting it report what the sweep did.
            if (Interlocked.Exchange(ref _holdNextStop, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult();
            await _release.Task;
        }

        public Task RebuildAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
