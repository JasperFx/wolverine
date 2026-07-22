using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
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
/// GH-3376 hygiene: EventStoreAgents used to subscribe every observer to a database daemon's Tracker and
/// never unsubscribe (the standing "do we need to care about un-subscribing?" TODO), so a node kept
/// observing databases it no longer owned until the whole store was disposed. The subscriptions are now
/// released when the last of a database's agents stops on this node, and re-established if the database is
/// reassigned back. The daemon itself stays cached and undisposed - it is shared with IProjectionCoordinator
/// callers (via AllDaemonsAsync) and in-flight rebuilds, so tearing it down here would be a use-after-dispose.
///
/// These assert on what the observer actually hears, against a real ShardStateTracker, rather than on the
/// subscription bookkeeping - the leak only matters if a released observer keeps getting fed.
/// </summary>
public class event_store_agents_observer_subscriptions
{
    private readonly DatabaseId theDatabaseId = new("localhost", "claims1");
    private readonly IProjectionDaemon theDaemon = Substitute.For<IProjectionDaemon>();
    private readonly ShardStateTracker theTracker = new(NullLogger.Instance);
    private readonly RecordingObserver theObserver = new();
    private readonly EventStoreAgents theAgents;

    public event_store_agents_observer_subscriptions()
    {
        theDaemon.Tracker.Returns(theTracker);

        var usage = new EventStoreUsage
        {
            Database = new DatabaseUsage
            {
                Databases =
                {
                    new DatabaseDescriptor { ServerName = "localhost", DatabaseName = "claims1" }
                }
            },
            Subscriptions =
            {
                new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                {
                    Lifecycle = ProjectionLifecycle.Async,
                    ShardNames = new[] { ShardName.Compose("provided-cares"), ShardName.Compose("other-shard") }
                }
            }
        };

        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity("main", "marten"));
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(usage));
        store.BuildProjectionDaemonAsync(Arg.Any<DatabaseId>())
            .Returns(new ValueTask<IProjectionDaemon>(theDaemon));

        theAgents = new EventStoreAgents(store, [theObserver]);
    }

    // The shard cache is keyed by ShardName.RelativeUrl, so ask ShardName for the path rather than
    // hand-writing the format.
    private static string PathFor(string projectionName) => ShardName.Compose(projectionName).RelativeUrl;

    private async Task<EventSubscriptionAgent> startAgentAsync(string projectionName)
    {
        var shardPath = PathFor(projectionName);
        var uri = new Uri($"event://marten/main/localhost/claims1/{shardPath}");
        var agent = await theAgents.BuildAgentAsync(uri, theDatabaseId, shardPath);
        await agent.StartAsync(CancellationToken.None);
        return agent;
    }

    // The tracker publishes to observers through a block, so give it a beat to drain.
    private async Task<bool> observerHearsAsync(long highWaterMark)
    {
        var before = theObserver.Count;
        await theTracker.MarkHighWaterAsync(highWaterMark);

        using var cts = new CancellationTokenSource(2.Seconds());
        while (!cts.IsCancellationRequested)
        {
            if (theObserver.Count > before) return true;
            await Task.Delay(25);
        }

        return theObserver.Count > before;
    }

    [Fact]
    public async Task stops_feeding_the_observer_once_the_last_agent_for_a_database_stops()
    {
        var agent = await startAgentAsync("provided-cares");
        (await observerHearsAsync(5)).ShouldBeTrue("a running agent's database should feed its observers");

        await agent.StopAsync(CancellationToken.None);

        (await observerHearsAsync(10)).ShouldBeFalse(
            "the node stopped the database's last agent, so it must no longer observe that database");
    }

    [Fact]
    public async Task keeps_feeding_the_observer_while_another_agent_for_the_database_runs()
    {
        var first = await startAgentAsync("provided-cares");
        var second = await startAgentAsync("other-shard");

        await first.StopAsync(CancellationToken.None);

        (await observerHearsAsync(5)).ShouldBeTrue("a second agent for this database is still running");

        await second.StopAsync(CancellationToken.None);

        (await observerHearsAsync(10)).ShouldBeFalse();
    }

    [Fact]
    public async Task re_subscribes_when_a_released_database_is_reassigned_to_this_node()
    {
        var agent = await startAgentAsync("provided-cares");
        await agent.StopAsync(CancellationToken.None);
        (await observerHearsAsync(5)).ShouldBeFalse();

        // Reassigned back. The daemon is served from cache, so without re-subscribing the observers
        // would stay deaf for the rest of the process lifetime.
        await startAgentAsync("provided-cares");

        (await observerHearsAsync(10)).ShouldBeTrue("a reassigned database must feed its observers again");
    }

    [Fact]
    public async Task does_not_tear_down_the_shared_daemon_when_the_last_agent_stops()
    {
        var agent = await startAgentAsync("provided-cares");
        await agent.StopAsync(CancellationToken.None);

        // The daemon is handed out to IProjectionCoordinator callers and held across rebuilds, so
        // releasing this node's interest in the database must not stop or dispose it underneath them.
        await theDaemon.DidNotReceive().StopAllAsync();
        (await theAgents.FindDaemonAsync(theDatabaseId)).ShouldBeSameAs(theDaemon);
    }

    [Fact]
    public async Task stamps_the_assigned_node_number_onto_the_owned_daemon_tracker()
    {
        // marten#5001: when this node owns a database's daemon, its assigned node number must ride onto the
        // tracker so it stamps every published ShardState into the running_on_node telemetry column.
        theAgents.AssignedNodeNumber = 7;

        await theAgents.FindDaemonAsync(theDatabaseId);

        theTracker.AssignedNodeNumber.ShouldBe(7);
    }

    [Fact]
    public async Task leaves_the_tracker_node_unset_when_this_node_has_no_assignment()
    {
        // Default AssignedNodeNumber == 0 (e.g. before any agent assignment) leaves the tracker alone.
        await theAgents.FindDaemonAsync(theDatabaseId);

        theTracker.AssignedNodeNumber.ShouldBe(0);
    }

    private class RecordingObserver : IObserver<ShardState>
    {
        private readonly ConcurrentBag<ShardState> _states = [];

        public int Count => _states.Count;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value) => _states.Add(value);
    }
}
