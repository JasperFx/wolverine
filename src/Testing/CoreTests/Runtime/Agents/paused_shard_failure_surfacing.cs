using JasperFx;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;
using IProjectionDaemon = JasperFx.Events.Daemon.IProjectionDaemon;
using JasperFxSubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Coverage for GH-3637 / GH-3638 (WO-8), the Wolverine half of JasperFx/jasperfx#565. When a shard hits
/// an ApplyEventException under a non-skipping error policy the daemon pauses it, and until now the
/// assignment plane could see only <see cref="AgentStatus.Paused"/> — never why. The anti-thrash guards
/// meant the shard wasn't restart-looped, but nothing surfaced it either: progress silently flatlined and
/// the only trace was a log line on one node. Now the classified <see cref="ShardFailure"/> rides through
/// the wrapper into the health check and the observer plane, and a failure that will recur on the same
/// event is no longer swept back up by the GH-3519 wedge recovery.
/// </summary>
public class paused_shard_failure_surfacing
{
    private static ShardFailure FailureOf(ShardFailureCategory category, long? sequence = 42)
    {
        return new ShardFailure
        {
            Category = category,
            ExceptionType = "JasperFx.Events.Daemon.ApplyEventException",
            RootExceptionType = "System.NullReferenceException",
            Message = "Object reference not set to an instance of an object.",
            Detail = "JasperFx.Events.Daemon.ApplyEventException: ... ---> System.NullReferenceException: ...",
            OccurredAt = DateTimeOffset.UtcNow,
            Event = sequence == null
                ? null
                : new EventFailureDetails { Sequence = sequence.Value, EventTypeName = "trip_started" }
        };
    }

    public class wrapper_delegation
    {
        private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
        private readonly JasperFxSubscriptionAgent _inner = Substitute.For<JasperFxSubscriptionAgent>();
        private readonly EventSubscriptionAgent _agent;

        public wrapper_delegation()
        {
            _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
            _agent = new EventSubscriptionAgent(
                new Uri("event-subscriptions://marten/incident/all"), new ShardName("Incident"), _daemon);
        }

        [Fact]
        public void reports_no_failure_before_it_has_ever_started()
        {
            // Nothing to delegate to yet. This is the shape every non-tracking implementation reads as,
            // and it must never throw.
            _agent.Failure.ShouldBeNull();
        }

        [Fact]
        public async Task delegates_the_failure_to_the_live_inner_agent()
        {
            var failure = FailureOf(ShardFailureCategory.ApplyEvent);
            _inner.Failure.Returns(failure);

            await _agent.StartAsync(CancellationToken.None);

            _agent.Failure.ShouldBeSameAs(failure);
        }

        [Fact]
        public async Task a_recovered_shard_stops_reporting_the_old_failure()
        {
            _inner.Failure.Returns(FailureOf(ShardFailureCategory.ApplyEvent));
            await _agent.StartAsync(CancellationToken.None);

            // The daemon clears Failure when the agent starts or replays. Because the wrapper reads
            // through rather than caching, that clearing is visible here immediately — a cached copy
            // would have kept alerting on a shard that had already recovered.
            _inner.Failure.Returns((ShardFailure?)null);

            _agent.Failure.ShouldBeNull();
        }
    }

    public class health_check_reporting
    {
        private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
        private readonly JasperFxSubscriptionAgent _inner = Substitute.For<JasperFxSubscriptionAgent>();
        private readonly EventSubscriptionAgent _agent;

        public health_check_reporting()
        {
            _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
            _agent = new EventSubscriptionAgent(
                new Uri("event-subscriptions://marten/incident/all"), new ShardName("Incident"), _daemon);
        }

        private Task<HealthCheckResult> checkAsync()
            => _agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        [Fact]
        public async Task a_paused_shard_reports_the_category_the_failing_event_and_the_root_exception()
        {
            _inner.Status.Returns(AgentStatus.Paused);
            _inner.Failure.Returns(FailureOf(ShardFailureCategory.ApplyEvent));
            await _agent.StartAsync(CancellationToken.None);

            var result = await checkAsync();

            result.Status.ShouldBe(HealthStatus.Unhealthy);
            var description = result.Description!;

            // The whole point of WO-8: the difference between an alert an operator can act on and one
            // they have to go dig for. Category says what kind of problem, the sequence says exactly
            // where it stopped, and the ROOT exception type names the actual fault rather than the
            // ApplyEventException wrapper around it.
            description.ShouldContain("ApplyEvent");
            description.ShouldContain("42");
            description.ShouldContain("trip_started");
            description.ShouldContain("System.NullReferenceException");
        }

        [Fact]
        public async Task a_shard_the_daemon_stopped_out_of_order_reports_its_reason_too()
        {
            // ProgressionOutOfOrder is the one category where the daemon STOPS rather than pauses, so
            // the reason has to survive the Stopped branch as well.
            _inner.Status.Returns(AgentStatus.Stopped);
            _inner.Failure.Returns(FailureOf(ShardFailureCategory.ProgressionOutOfOrder, sequence: null));
            await _agent.StartAsync(CancellationToken.None);

            var result = await checkAsync();

            result.Status.ShouldBe(HealthStatus.Unhealthy);
            result.Description!.ShouldContain("ProgressionOutOfOrder");
        }

        [Fact]
        public async Task a_paused_shard_with_no_reported_reason_keeps_the_old_message()
        {
            // An inner agent that doesn't track failures reads exactly as it did before this change.
            _inner.Status.Returns(AgentStatus.Paused);
            _inner.Failure.Returns((ShardFailure?)null);
            await _agent.StartAsync(CancellationToken.None);

            var result = await checkAsync();

            result.Status.ShouldBe(HealthStatus.Unhealthy);
            result.Description!.ShouldContain("paused due to errors");
        }
    }

    public class restart_suppression
    {
        private readonly WolverineOptions _options;
        private readonly IWolverineRuntime _runtime;
        private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();
        private readonly CancellationTokenSource _cancellation = new();

        public restart_suppression()
        {
            _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
            _options.Durability.Mode = DurabilityMode.Solo;
            _options.Durability.DurabilityAgentEnabled = false;
            _options.Durability.CheckAssignmentPeriod = 1.Hours();

            _runtime = Substitute.For<IWolverineRuntime>();
            _runtime.Options.Returns(_options);
            _runtime.DurabilitySettings.Returns(_options.Durability);
            _runtime.Observer.Returns(_observer);
        }

        private NodeAgentController controllerFor(params FakeSubscriptionAgent[] agents)
        {
            var family = new FakeSubscriptionAgentFamily("event-subscriptions");
            foreach (var agent in agents)
            {
                family.Add(agent);
            }

            return new NodeAgentController(_runtime, Substitute.For<INodeAgentPersistence>(), [family],
                NullLogger<NodeAgentController>.Instance, _cancellation.Token);
        }

        [Fact]
        public async Task does_not_restart_a_shard_stopped_on_a_failure_that_will_recur()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);
            agent.StartCount.ShouldBe(1);

            // The daemon stopped this shard on a progression row two processes are fighting over.
            // Restarting adds a third contender; the GH-3519 wedge recovery must stand down here.
            agent.SimulateFailure(AgentStatus.Stopped, FailureOf(ShardFailureCategory.ProgressionOutOfOrder));

            await controller.StartAgentAsync(uri);

            agent.StartCount.ShouldBe(1);
            agent.StopCount.ShouldBe(0);
            await _observer.Received(1).AgentPaused(uri, Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task keeps_reporting_at_most_once_per_transition_into_failure()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);
            agent.SimulateFailure(AgentStatus.Stopped, FailureOf(ShardFailureCategory.ApplyEvent));

            // The leader re-issues AssignAgent on every 30s reevaluation for as long as the grid sees
            // this agent as not running. One alert per failure, not one every tick.
            await controller.StartAgentAsync(uri);
            await controller.StartAgentAsync(uri);
            await controller.StartAgentAsync(uri);

            await _observer.Received(1).AgentPaused(uri, Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task still_restarts_a_shard_whose_failure_could_clear_on_its_own()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            // A database outage or a timeout is exactly what restart-on-failure exists for.
            agent.SimulateFailure(AgentStatus.Stopped, FailureOf(ShardFailureCategory.Other, sequence: null));

            await controller.StartAgentAsync(uri);

            agent.StartCount.ShouldBe(2);
        }

        [Fact]
        public async Task still_restarts_a_wedged_shard_that_reports_no_failure()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            // GH-3519's case, unchanged: the shard died with nothing to report, so the wedge recovery
            // still owns it.
            agent.SimulateFailure(AgentStatus.Stopped, null);

            await controller.StartAgentAsync(uri);

            agent.StartCount.ShouldBe(2);
        }
    }

    public class local_sweep
    {
        private readonly WolverineOptions _options;
        private readonly IWolverineRuntime _runtime;
        private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();
        private readonly CancellationTokenSource _cancellation = new();

        public local_sweep()
        {
            _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
            _options.Durability.Mode = DurabilityMode.Solo;
            _options.Durability.DurabilityAgentEnabled = false;
            _options.Durability.CheckAssignmentPeriod = 1.Hours();

            _runtime = Substitute.For<IWolverineRuntime>();
            _runtime.Options.Returns(_options);
            _runtime.DurabilitySettings.Returns(_options.Durability);
            _runtime.Observer.Returns(_observer);
        }

        private NodeAgentController controllerFor(params FakeSubscriptionAgent[] agents)
        {
            var family = new FakeSubscriptionAgentFamily("event-subscriptions");
            foreach (var agent in agents)
            {
                family.Add(agent);
            }

            return new NodeAgentController(_runtime, Substitute.For<INodeAgentPersistence>(), [family],
                NullLogger<NodeAgentController>.Instance, _cancellation.Token);
        }

        [Fact]
        public async Task surfaces_a_locally_owned_shard_the_daemon_paused()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            // Paused is the ApplyEventException shape, and it never reaches the restart path at all --
            // StartAgentAsync treats any non-Stopped agent as running and returns early. Without this
            // sweep nothing in the process would ever have mentioned it again.
            agent.SimulateFailure(AgentStatus.Paused, FailureOf(ShardFailureCategory.ApplyEvent));

            await controller.ReportFailedLocalAgentsAsync();

            await _observer.Received(1)
                .AgentPaused(uri, Arg.Is<ShardFailure?>(f => f!.Category == ShardFailureCategory.ApplyEvent));
        }

        [Fact]
        public async Task reports_once_per_transition_not_once_per_tick()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);
            agent.SimulateFailure(AgentStatus.Paused, FailureOf(ShardFailureCategory.ApplyEvent));

            await controller.ReportFailedLocalAgentsAsync();
            await controller.ReportFailedLocalAgentsAsync();
            await controller.ReportFailedLocalAgentsAsync();

            await _observer.Received(1).AgentPaused(uri, Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task re_arms_after_the_shard_recovers()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);
            agent.SimulateFailure(AgentStatus.Paused, FailureOf(ShardFailureCategory.ApplyEvent));
            await controller.ReportFailedLocalAgentsAsync();

            // Operator skips the poison event and the shard runs again...
            agent.SimulateRecovered();
            await controller.ReportFailedLocalAgentsAsync();

            // ...and a NEW failure has to alert again rather than being swallowed as a duplicate.
            agent.SimulateFailure(AgentStatus.Paused, FailureOf(ShardFailureCategory.EventSerialization));
            await controller.ReportFailedLocalAgentsAsync();

            await _observer.Received(2).AgentPaused(uri, Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task says_nothing_about_a_healthy_shard()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            await controller.ReportFailedLocalAgentsAsync();

            await _observer.DidNotReceive().AgentPaused(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task says_nothing_about_a_shard_stopped_with_no_reason_to_report()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            // An ordinary stop, or a wedged shard GH-3519's recovery owns. Reporting it here would fire
            // on every deliberate stop and drown the signal this whole sweep exists to raise.
            agent.SimulateFailure(AgentStatus.Stopped, null);

            await controller.ReportFailedLocalAgentsAsync();

            await _observer.DidNotReceive().AgentPaused(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
        }
    }

    private class FakeSubscriptionAgentFamily : IAgentFamily
    {
        private readonly Dictionary<Uri, FakeSubscriptionAgent> _agents = new();

        public FakeSubscriptionAgentFamily(string scheme) => Scheme = scheme;

        public void Add(FakeSubscriptionAgent agent) => _agents[agent.Uri] = agent;

        public string Scheme { get; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => ValueTask.FromResult<IAgent>(_agents[uri]);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    private class FakeSubscriptionAgent : IEventSubscriptionAgent
    {
        public FakeSubscriptionAgent(Uri uri) => Uri = uri;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
        public ShardFailure? Failure { get; private set; }

        // Mimic the daemon flipping status and reason together underneath the wrapper, without going
        // through the controller's registration.
        public void SimulateFailure(AgentStatus status, ShardFailure? failure)
        {
            Status = status;
            Failure = failure;
        }

        public void SimulateRecovered()
        {
            Status = AgentStatus.Running;
            Failure = null;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            Status = AgentStatus.Running;
            Failure = null;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            Status = AgentStatus.Stopped;
            return Task.CompletedTask;
        }

        public Task RebuildAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
