using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;
using JasperFxSubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-5003. A projection rebuild hard-stops the continuous agent and tears down the projection's state
/// before it replays. When the rebuild threw, nothing restarted the shard: the live-agent path skipped
/// its resume, and the registered-projection path (GH-3163) never resumed at all, leaving it to
/// NodeAgentController's sweep -- which only reports a shard whose pre-rebuild agent carried an Other
/// failure, so that one stayed stopped until the process restarted.
/// </summary>
public class failed_rebuild_resumes_continuous_execution
{
    public class through_the_live_agent
    {
        private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
        private readonly ShardName _shardName = new("Incident");
        private readonly EventSubscriptionAgent _agent;

        public through_the_live_agent()
        {
            _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(Substitute.For<JasperFxSubscriptionAgent>());
            _agent = new EventSubscriptionAgent(
                new Uri("event-subscriptions://marten/incident/all"), _shardName, _daemon);
        }

        [Fact]
        public async Task a_rebuild_that_throws_still_resumes_continuous_execution_and_still_throws()
        {
            await _agent.StartAsync(CancellationToken.None);
            _daemon.RebuildProjectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("the connection pool has been exhausted"));

            var ex = await Should.ThrowAsync<InvalidOperationException>(() => _agent.RebuildAsync(CancellationToken.None));
            ex.Message.ShouldBe("the connection pool has been exhausted");

            // Once for the original start, once for the resume after the failed rebuild
            await _daemon.Received(2).StartAgentAsync(_shardName, Arg.Any<CancellationToken>());
            _agent.Status.ShouldBe(AgentStatus.Running);
        }

        [Fact]
        public async Task a_failed_resume_does_not_hide_why_the_rebuild_failed()
        {
            await _agent.StartAsync(CancellationToken.None);
            _daemon.RebuildProjectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("rebuild failed"));
            _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new TimeoutException("resume failed"));

            var ex = await Should.ThrowAsync<InvalidOperationException>(() => _agent.RebuildAsync(CancellationToken.None));
            ex.Message.ShouldBe("rebuild failed");
        }

        [Fact]
        public async Task a_cancelled_rebuild_is_not_resumed()
        {
            await _agent.StartAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            _daemon.RebuildProjectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await cancellation.CancelAsync();
                    throw new OperationCanceledException(cancellation.Token);
                });

            await Should.ThrowAsync<OperationCanceledException>(() => _agent.RebuildAsync(cancellation.Token));

            // Only the original start: the host is shutting down or somebody called the rebuild off
            await _daemon.Received(1).StartAgentAsync(_shardName, Arg.Any<CancellationToken>());
        }

        [Fact]
        public void is_rebuilding_comes_from_the_daemon()
        {
            _agent.IsRebuilding.ShouldBeFalse();

            _daemon.IsRebuilding(_shardName).Returns(true);

            _agent.IsRebuilding.ShouldBeTrue();
        }
    }

    public class through_the_registered_projection_rebuild
    {
        private readonly DatabaseId theDatabaseId = new("localhost", "claims1");
        private readonly IProjectionDaemon theDaemon = Substitute.For<IProjectionDaemon>();
        private readonly EventStoreAgents theAgents;

        public through_the_registered_projection_rebuild()
        {
            theDaemon.Tracker.Returns(new ShardStateTracker(NullLogger.Instance));
            theDaemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>())
                .Returns(Substitute.For<JasperFxSubscriptionAgent>());

            var usage = new EventStoreUsage
            {
                Database = new DatabaseUsage
                {
                    MainDatabase = new DatabaseDescriptor { ServerName = "localhost", DatabaseName = "claims1" }
                },
                Subscriptions =
                {
                    new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                    {
                        Name = "trips",
                        Lifecycle = ProjectionLifecycle.Async,
                        ShardNames = [ShardName.Compose("trips")]
                    },
                    new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                    {
                        Name = "invoices",
                        Lifecycle = ProjectionLifecycle.Async,
                        ShardNames = [ShardName.Compose("invoices")]
                    }
                }
            };

            var store = Substitute.For<IEventStore>();
            store.Identity.Returns(new EventStoreIdentity("main", "marten"));
            store.StubUsage(usage);
            store.BuildProjectionDaemonAsync(Arg.Any<DatabaseId>())
                .Returns(new ValueTask<IProjectionDaemon>(theDaemon));

            theAgents = new EventStoreAgents(store, []);
        }

        private async Task<EventSubscriptionAgent> startAgentAsync(string projectionName)
        {
            var shardPath = ShardName.Compose(projectionName).RelativeUrl;
            var uri = new Uri($"event://marten/main/localhost/claims1/{shardPath}");
            var agent = await theAgents.BuildAgentAsync(uri, theDatabaseId, shardPath);
            await agent.StartAsync(CancellationToken.None);
            return agent;
        }

        private Task startsOf(string projectionName, int expected)
            => theDaemon.Received(expected).StartAgentAsync(
                Arg.Is<ShardName>(x => x.Name == projectionName), Arg.Any<CancellationToken>());

        [Fact]
        public async Task resumes_this_nodes_agents_for_the_rebuilt_projection_only()
        {
            await startAgentAsync("trips");
            await startAgentAsync("invoices");

            (await theAgents.TryRebuildRegisteredProjectionAsync("trips:All", null, CancellationToken.None))
                .ShouldBeTrue();

            await startsOf("trips", 2);
            await startsOf("invoices", 1);
        }

        [Fact]
        public async Task resumes_them_when_the_rebuild_throws_and_still_throws()
        {
            var trips = await startAgentAsync("trips");
            theDaemon.RebuildProjectionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("the connection pool has been exhausted"));

            await Should.ThrowAsync<InvalidOperationException>(() =>
                theAgents.TryRebuildRegisteredProjectionAsync("trips:All", null, CancellationToken.None));

            await startsOf("trips", 2);
            trips.Status.ShouldBe(AgentStatus.Running);
        }

        [Fact]
        public async Task does_not_resume_an_agent_that_was_stopped_on_purpose()
        {
            var trips = await startAgentAsync("trips");
            await trips.StopAsync(CancellationToken.None);

            (await theAgents.TryRebuildRegisteredProjectionAsync("trips:All", null, CancellationToken.None))
                .ShouldBeTrue();

            await startsOf("trips", 1);
            trips.Status.ShouldBe(AgentStatus.Stopped);
        }

        [Fact]
        public async Task starts_nothing_for_a_projection_with_no_agent_on_this_node()
        {
            (await theAgents.TryRebuildRegisteredProjectionAsync("trips:All", null, CancellationToken.None))
                .ShouldBeTrue();

            await theDaemon.DidNotReceive().StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>());
        }
    }
}
