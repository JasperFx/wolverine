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

            // A database outage or a timeout is exactly what restart-on-failure exists for. GH-4986: classified from
            // the exception by ShardFailure.IsTransient, not by the category alone.
            agent.SimulateFailure(AgentStatus.Stopped,
                FailureOf(ShardFailureCategory.Other, sequence: null) with { IsTransient = true });

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

            // A wedged shard with nothing to report is not a FAILURE to surface -- reporting it would
            // fire on every deliberate stop and drown the signal this whole sweep exists to raise.
            // GH-4193 does now RESTART it here (see below); this test pins that it is still not
            // reported as a failure while doing so.
            agent.SimulateFailure(AgentStatus.Stopped, null);

            await controller.ReportFailedLocalAgentsAsync();

            await _observer.DidNotReceive().AgentPaused(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
        }

        // GH-4193. The sweep used to `continue` past a Stopped agent with no failure, on the grounds
        // that "GH-3519's recovery in StartAgentAsync owns that one". It never got the chance:
        // StartAgentAsync only runs when the leader emits an assignment command, and
        // AssignmentGrid.Agent.TryBuildAssignmentCommand returns false while AssignedNode ==
        // OriginalNode. A local stop leaves the persisted assignment row alone, so the two stay equal
        // and no command is ever built -- both recovery paths deferred to each other and the shard's
        // sequence never moved again. Reachable deliberately through
        // EventStoreAgents.TryRebuildRegisteredProjectionAsync (GH-3163).

        [Fact]
        public async Task restarts_a_locally_owned_shard_that_stopped_with_nothing_to_report()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);
            agent.StartCount.ShouldBe(1);

            agent.SimulateFailure(AgentStatus.Stopped, null);

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(2);
        }

        // GH-5003. A rebuild hard-stops the continuous agent and leaves its old failure in place, so for
        // the whole replay the shard reads exactly like the stopped shards the sweep restarts. Restarting
        // it would put a continuous agent on the same progression row as the rebuild.

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task leaves_a_shard_alone_while_its_projection_is_being_rebuilt(bool hadTransientFailure)
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            agent.IsRebuilding = true;
            agent.SimulateFailure(AgentStatus.Stopped, hadTransientFailure
                ? FailureOf(ShardFailureCategory.Other, sequence: null) with
                {
                    OccurredAt = DateTimeOffset.UtcNow.Subtract(1.Minutes())
                }
                : null);

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(1);
            agent.StopCount.ShouldBe(0);
            await _observer.DidNotReceive().AgentPaused(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task picks_a_stopped_shard_back_up_once_its_rebuild_is_over()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            agent.IsRebuilding = true;
            agent.SimulateFailure(AgentStatus.Stopped, null);
            await controller.ReportFailedLocalAgentsAsync();
            agent.StartCount.ShouldBe(1);

            // The rebuild ended without handing the shard back; the ordinary GH-4193 recovery applies again
            agent.IsRebuilding = false;
            await controller.ReportFailedLocalAgentsAsync();
            agent.StartCount.ShouldBe(2);
        }

        [Fact]
        public async Task restarts_it_once_per_transition_not_once_per_tick()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri) { StaysStoppedAfterStart = true };
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);
            agent.SimulateFailure(AgentStatus.Stopped, null);

            // A shard that will not come back up must not be re-driven on every health tick.
            await controller.ReportFailedLocalAgentsAsync();
            await controller.ReportFailedLocalAgentsAsync();
            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(2);
        }

        [Fact]
        public async Task re_arms_the_restart_after_the_shard_is_seen_running_again()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            agent.SimulateFailure(AgentStatus.Stopped, null);
            await controller.ReportFailedLocalAgentsAsync();   // restart #1
            await controller.ReportFailedLocalAgentsAsync();   // observes Running, clears the ledger

            agent.SimulateFailure(AgentStatus.Stopped, null);
            await controller.ReportFailedLocalAgentsAsync();   // restart #2

            agent.StartCount.ShouldBe(3);
        }

        // GH-4986. A transient failure (ShardFailure.IsTransient: a pool exhausted during a large rebuild, a timeout,
        // a failover) pauses the shard and disposes it, so nothing in the daemon resumes it. Marten's own
        // ProjectionCoordinator restarts a paused shard once it has been paused for a health-check interval; under
        // Wolverine-managed distribution that coordinator does not run, and the sweep used to report the failure as
        // unrecoverable and leave the shard paused until the process restarted. Every timing fact here runs on the
        // controller's own clock.

        private sealed class ManualClock(DateTimeOffset start) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = start;
            public override DateTimeOffset GetUtcNow() => Now;
            public void Advance(TimeSpan by) => Now = Now.Add(by);
        }

        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

        private NodeAgentController clockedControllerFor(params FakeSubscriptionAgent[] agents)
        {
            var controller = controllerFor(agents);
            controller.TimeProvider = _clock;
            return controller;
        }

        // A failure the daemon reported just now, as JasperFx classifies a pool timeout
        private ShardFailure transientFailure(ShardFailureCategory category = ShardFailureCategory.Other)
        {
            return FailureOf(category, sequence: category == ShardFailureCategory.Other ? null : 42) with
            {
                OccurredAt = _clock.GetUtcNow(),
                IsTransient = true
            };
        }

        private async Task<(FakeSubscriptionAgent agent, NodeAgentController controller)> runningAgentAsync()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = clockedControllerFor(agent);
            await controller.StartAgentAsync(uri);
            return (agent, controller);
        }

        [Fact]
        public async Task restarts_a_paused_shard_that_failed_on_a_transient_fault()
        {
            var (agent, controller) = await runningAgentAsync();

            agent.SimulateFailure(AgentStatus.Paused, transientFailure());
            _clock.Advance(_options.Durability.HealthCheckPollingTime);

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(2);
            agent.Status.ShouldBe(AgentStatus.Running);
        }

        [Fact]
        public async Task waits_a_health_check_interval_before_restarting_it()
        {
            var (agent, controller) = await runningAgentAsync();

            // The daemon has just given up on its own retries. Restarting on the same tick would hammer whatever
            // is down; the first wait is the health-check interval.
            agent.SimulateFailure(AgentStatus.Paused, transientFailure());

            _clock.Advance(_options.Durability.HealthCheckPollingTime - 1.Milliseconds());
            await controller.ReportFailedLocalAgentsAsync();
            agent.StartCount.ShouldBe(1);

            _clock.Advance(1.Milliseconds());
            await controller.ReportFailedLocalAgentsAsync();
            agent.StartCount.ShouldBe(2);

            await _observer.Received(1).AgentPaused(agent.Uri, Arg.Any<ShardFailure?>());
        }

        [Fact]
        public async Task the_wait_doubles_for_every_restart_until_the_ceiling()
        {
            var (agent, controller) = await runningAgentAsync();
            var interval = _options.Durability.HealthCheckPollingTime;
            _options.Durability.TransientFailureRestartMaxBackoff = interval * 4;

            // A shard on a starved pool: it comes back up, then pauses again straight away
            async Task failAndWaitAsync(TimeSpan expectedWait, int expectedStarts)
            {
                agent.SimulateFailure(AgentStatus.Paused, transientFailure());

                _clock.Advance(expectedWait - 1.Milliseconds());
                await controller.ReportFailedLocalAgentsAsync();
                agent.StartCount.ShouldBe(expectedStarts - 1, $"restarted before {expectedWait}");

                _clock.Advance(1.Milliseconds());
                await controller.ReportFailedLocalAgentsAsync();
                agent.StartCount.ShouldBe(expectedStarts, $"not restarted after {expectedWait}");

                // Seen Running for a tick, which is not running cleanly for long
                await controller.ReportFailedLocalAgentsAsync();
            }

            await failAndWaitAsync(interval, 2);
            await failAndWaitAsync(interval * 2, 3);
            await failAndWaitAsync(interval * 4, 4);

            // Capped
            await failAndWaitAsync(interval * 4, 5);
        }

        [Fact]
        public async Task the_wait_starts_over_once_the_shard_has_run_cleanly_for_longer_than_the_ceiling()
        {
            var (agent, controller) = await runningAgentAsync();
            var interval = _options.Durability.HealthCheckPollingTime;

            agent.SimulateFailure(AgentStatus.Paused, transientFailure());
            _clock.Advance(interval);
            await controller.ReportFailedLocalAgentsAsync();

            agent.SimulateFailure(AgentStatus.Paused, transientFailure());
            _clock.Advance(interval * 2);
            await controller.ReportFailedLocalAgentsAsync();
            agent.StartCount.ShouldBe(3);

            // Runs cleanly past the ceiling
            _clock.Advance(_options.Durability.TransientFailureRestartMaxBackoff);
            await controller.ReportFailedLocalAgentsAsync();

            agent.SimulateFailure(AgentStatus.Paused, transientFailure());
            _clock.Advance(interval);
            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(4);
        }

        [Fact]
        public async Task never_restarts_a_stopped_shard_even_with_a_transient_failure()
        {
            // A rebuild hard-stops a shard and leaves its old failure in place, so Stopped plus a failure can be a
            // rebuild in flight, and a restart would put a continuous agent on the rebuild's progression row
            var (agent, controller) = await runningAgentAsync();

            agent.SimulateFailure(AgentStatus.Stopped, transientFailure());
            _clock.Advance(1.Hours());

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(1);
        }

        [Fact]
        public async Task leaves_a_shard_paused_on_a_permanent_failure_that_is_still_category_other()
        {
            // The case the old category check got wrong: a missing column is Other too, and restarting it every
            // health-check interval would loop forever
            var (agent, controller) = await runningAgentAsync();

            agent.SimulateFailure(AgentStatus.Paused, transientFailure() with { IsTransient = false });
            _clock.Advance(1.Hours());

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(1);
        }

        [Fact]
        public async Task leaves_a_shard_paused_on_a_failure_that_will_recur()
        {
            var (agent, controller) = await runningAgentAsync();

            agent.SimulateFailure(AgentStatus.Paused,
                FailureOf(ShardFailureCategory.ApplyEvent) with { OccurredAt = _clock.GetUtcNow() });
            _clock.Advance(1.Hours());

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(1);
        }

        [Fact]
        public async Task restarts_a_transient_fault_inside_a_single_events_apply()
        {
            // A timeout in the projection's own lookup is still pinned to one event, and still worth a retry
            var (agent, controller) = await runningAgentAsync();

            agent.SimulateFailure(AgentStatus.Paused, transientFailure(ShardFailureCategory.ApplyEvent));
            _clock.Advance(_options.Durability.HealthCheckPollingTime);

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(2);
        }

        [Fact]
        public async Task never_restarts_a_progression_conflict_whatever_the_exception_says()
        {
            // Two processes on one shard; a restart adds a third
            var (agent, controller) = await runningAgentAsync();

            agent.SimulateFailure(AgentStatus.Paused, transientFailure(ShardFailureCategory.ProgressionOutOfOrder));
            _clock.Advance(1.Hours());

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(1);
        }

        private sealed class SingleAgentFamily(IAgent agent) : IAgentFamily
        {
            public string Scheme => agent.Uri.Scheme;
            public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync() => ValueTask.FromResult<IReadOnlyList<Uri>>([agent.Uri]);
            public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime) => ValueTask.FromResult(agent);
            public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync() => ValueTask.FromResult<IReadOnlyList<Uri>>([agent.Uri]);
            public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
        }

        [Fact]
        public async Task tells_observers_about_the_restart()
        {
            // The stall-restart path raises OnRestarted; this one used to restart silently, so CritterWatch showed
            // the Critical "paused" alert until the shard happened to read Running again
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var shard = new ShardName("Incident");

            var inner = Substitute.For<JasperFxSubscriptionAgent>();
            inner.Status.Returns(AgentStatus.Running);

            var daemon = Substitute.For<IProjectionDaemon>();
            daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(inner);

            var agent = new EventSubscriptionAgent(uri, shard, daemon);
            var restarts = new List<string>();
            agent.OnRestarted = (_, reason, _) => restarts.Add(reason);

            var controller = new NodeAgentController(_runtime, Substitute.For<INodeAgentPersistence>(),
                [new SingleAgentFamily(agent)], NullLogger<NodeAgentController>.Instance, _cancellation.Token)
            {
                TimeProvider = _clock
            };

            await controller.StartAgentAsync(uri);

            inner.Status.Returns(AgentStatus.Paused);
            inner.Failure.Returns(transientFailure());
            _clock.Advance(_options.Durability.HealthCheckPollingTime);

            await controller.ReportFailedLocalAgentsAsync();

            await daemon.Received(2).StartAgentAsync(shard, Arg.Any<CancellationToken>());
            restarts.ShouldHaveSingleItem().ShouldContain("transient");
        }

        [Fact]
        public async Task on_a_warmup_node_the_restart_comes_back_in_rebuild_mode()
        {
            // GH-4569: a warm-up shard paused on a pool timeout during its long rebuild comes back through
            // StartAgentInRebuildModeAsync, which resumes from its committed progress (JasperFx 2.88.0)
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var shard = new ShardName("Incident");

            var inner = Substitute.For<JasperFxSubscriptionAgent>();
            inner.Status.Returns(AgentStatus.Running);

            var daemon = Substitute.For<IProjectionDaemon>();
            daemon.StartAgentInRebuildModeAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(inner);

            var agent = new EventSubscriptionAgent(uri, shard, daemon) { StartsInRebuildMode = true };

            var controller = new NodeAgentController(_runtime, Substitute.For<INodeAgentPersistence>(),
                [new SingleAgentFamily(agent)], NullLogger<NodeAgentController>.Instance, _cancellation.Token)
            {
                TimeProvider = _clock
            };

            await controller.StartAgentAsync(uri);

            inner.Status.Returns(AgentStatus.Paused);
            inner.Failure.Returns(transientFailure());
            _clock.Advance(_options.Durability.HealthCheckPollingTime);

            await controller.ReportFailedLocalAgentsAsync();

            await daemon.Received(2).StartAgentInRebuildModeAsync(shard, Arg.Any<CancellationToken>());
            await daemon.DidNotReceive().StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task leaves_a_paused_shard_with_no_reported_reason_to_its_own_owner()
        {
            var uri = new Uri("event-subscriptions://marten/incident/all");
            var agent = new FakeSubscriptionAgent(uri);
            var controller = controllerFor(agent);

            await controller.StartAgentAsync(uri);

            // Paused with nothing to report is an owner that schedules its own resume -- an error
            // backoff, or the blue/green side-effect gate. Restarting it here would fight that owner,
            // which is what every anti-thrash guard in this class exists to prevent.
            agent.SimulateFailure(AgentStatus.Paused, null);

            await controller.ReportFailedLocalAgentsAsync();

            agent.StartCount.ShouldBe(1);
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

        // GH-4193: a shard that will not stay up. Lets a test hold the agent Stopped ACROSS a restart
        // attempt, which is the only way to observe the sweep's once-per-transition guard -- otherwise
        // the very next sweep sees Running, clears the ledger, and re-arms.
        public bool StaysStoppedAfterStart { get; set; }

        // GH-5003: the daemon is replaying this shard's projection right now
        public bool IsRebuilding { get; set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            if (!StaysStoppedAfterStart)
            {
                Status = AgentStatus.Running;
            }

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
