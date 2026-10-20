using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-5092: under Wolverine-managed distribution a monitoring caller (CritterWatch's high water loop report
/// and its "restart high water detection" command) has to reach the daemons THIS node is running without
/// building one for every database the store describes. AllDaemonsAsync used to build, cache and subscribe a
/// daemon per database, and DaemonForDatabase threw.
/// </summary>
public class event_store_agents_running_daemons
{
    private readonly DatabaseId theFirstDatabase = new("localhost", "claims1");
    private readonly DatabaseId theSecondDatabase = new("localhost", "claims2");
    private readonly IProjectionDaemon theFirstDaemon = Substitute.For<IProjectionDaemon>();
    private readonly IProjectionDaemon theSecondDaemon = Substitute.For<IProjectionDaemon>();
    private readonly IEventStore theStore = Substitute.For<IEventStore>();
    private readonly EventStoreAgents theAgents;

    public event_store_agents_running_daemons()
    {
        theFirstDaemon.Tracker.Returns(new ShardStateTracker(NullLogger.Instance) { DatabaseIdentifier = "first" });
        theSecondDaemon.Tracker.Returns(new ShardStateTracker(NullLogger.Instance) { DatabaseIdentifier = "second" });

        var usage = new EventStoreUsage
        {
            Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.StaticMultiple,
                Databases =
                {
                    new DatabaseDescriptor { ServerName = "localhost", DatabaseName = "claims1" },
                    new DatabaseDescriptor { ServerName = "localhost", DatabaseName = "claims2" }
                }
            },
            Subscriptions =
            {
                new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                {
                    Lifecycle = ProjectionLifecycle.Async,
                    ShardNames = new[] { ShardName.Compose("provided-cares") }
                }
            }
        };

        theStore.Identity.Returns(new EventStoreIdentity("main", "marten"));
        theStore.DatabaseCardinality.Returns(DatabaseCardinality.StaticMultiple);
        theStore.StubUsage(usage);
        theStore.BuildProjectionDaemonAsync(theFirstDatabase).Returns(new ValueTask<IProjectionDaemon>(theFirstDaemon));
        theStore.BuildProjectionDaemonAsync(theSecondDatabase).Returns(new ValueTask<IProjectionDaemon>(theSecondDaemon));

        theAgents = new EventStoreAgents(theStore, []);
    }

    private async Task<EventSubscriptionAgent> startAgentAsync(DatabaseId databaseId)
    {
        var shardPath = ShardName.Compose("provided-cares").RelativeUrl;
        var uri = new Uri($"event://marten/main/{databaseId.Identity}/{shardPath}");
        var agent = await theAgents.BuildAgentAsync(uri, databaseId, shardPath);
        await agent.StartAsync(CancellationToken.None);
        return agent;
    }

    [Fact]
    public async Task all_daemons_returns_only_the_databases_this_node_runs_agents_for()
    {
        await startAgentAsync(theFirstDatabase);

        var daemons = await theAgents.AllDaemonsAsync();

        daemons.ShouldHaveSingleItem().ShouldBeSameAs(theFirstDaemon);
    }

    [Fact]
    public async Task all_daemons_never_builds_a_daemon_for_a_database_this_node_does_not_own()
    {
        await startAgentAsync(theFirstDatabase);

        await theAgents.AllDaemonsAsync();
        await theAgents.AllDaemonsAsync();

        await theStore.DidNotReceive().BuildProjectionDaemonAsync(theSecondDatabase);
    }

    [Fact]
    public async Task all_daemons_is_empty_before_this_node_runs_anything()
    {
        (await theAgents.AllDaemonsAsync()).ShouldBeEmpty();

        await theStore.DidNotReceive().BuildProjectionDaemonAsync(Arg.Any<DatabaseId>());
    }

    [Fact]
    public async Task a_database_drops_out_once_its_last_local_agent_stops()
    {
        var agent = await startAgentAsync(theFirstDatabase);
        await agent.StopAsync(CancellationToken.None);

        // The daemon stays cached for the rebuild/borrow reasons GH-3376 documents, but this node no longer
        // runs anything there, so it is not one of "the daemons this node is running"
        (await theAgents.AllDaemonsAsync()).ShouldBeEmpty();
        theAgents.TryFindRunningDaemon("first").ShouldBeNull();
    }

    [Fact]
    public async Task a_cached_daemon_whose_high_water_loop_runs_still_counts()
    {
        // A daemon started wholesale through the coordinator's Resume/StartAllAsync runs with no agents
        var agent = await startAgentAsync(theFirstDatabase);
        await agent.StopAsync(CancellationToken.None);
        theFirstDaemon.IsRunning.Returns(true);

        (await theAgents.AllDaemonsAsync()).ShouldHaveSingleItem().ShouldBeSameAs(theFirstDaemon);
    }

    [Theory]
    [InlineData("first")] // the store's own database identifier, as the daemon's tracker carries it
    [InlineData("FIRST")]
    [InlineData("localhost.claims1")] // DatabaseId.Identity, as Wolverine's agent URIs carry it
    [InlineData("claims1")] // the database name
    public async Task daemon_for_database_returns_the_local_daemon(string identifier)
    {
        await startAgentAsync(theFirstDatabase);

        (await theAgents.DaemonForDatabase(identifier)).ShouldBeSameAs(theFirstDaemon);
    }

    [Fact]
    public async Task daemon_for_a_database_not_running_here_throws_and_builds_nothing()
    {
        await startAgentAsync(theFirstDatabase);

        var ex = await Should.ThrowAsync<DaemonNotRunningOnThisNodeException>(
            async () => await theAgents.DaemonForDatabase("second"));

        // Callers that caught the old unconditional NotSupportedException keep catching it
        ex.ShouldBeAssignableTo<NotSupportedException>();
        theAgents.TryFindRunningDaemon("second").ShouldBeNull();
        await theStore.DidNotReceive().BuildProjectionDaemonAsync(theSecondDatabase);
    }

    [Fact]
    public async Task daemon_for_main_database_is_not_answered_for_a_multi_database_store()
    {
        await startAgentAsync(theFirstDatabase);

        Should.Throw<DaemonNotRunningOnThisNodeException>(() => theAgents.DaemonForMainDatabase());
    }
}
