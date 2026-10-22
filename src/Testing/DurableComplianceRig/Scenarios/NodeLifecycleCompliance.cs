using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using Wolverine;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// Nodes leaving a cluster that shares one store and one queue: a graceful stop, a shutdown whose handler outlives the
/// drain, a kill, and handlers slower than <c>InboxStaleTime</c>. Store and transport agnostic; each concrete subclass
/// is bodyless and names its own feature. A store that implements <see cref="IKillableStore" /> gets a real kill,
/// any other store a disposal (which is a graceful stop, and says so in the run's log).
/// </summary>
public abstract class NodeLifecycleCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected NodeLifecycleCompliance(StoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        the = new DurableSteps(store, transport, output) { Mode = DurabilityMode.Balanced };
    }

    public ValueTask InitializeAsync() => new(the.PrepareLifecycleAsync());

    public async ValueTask DisposeAsync()
    {
        await the.BuryKilledNodesAsync();
        await the.DisposeAsync();
    }

    [BobcatSpec]
    public async Task a_graceful_stop_finishes_its_work_and_hands_over_the_rest()
    {
        the.TheClusterReactsWithinSeconds();
        the.ListenersRunInParallel(2);
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(12);
        the.MessageWaitsForGate(1, "drain");
        the.MessageWaitsForGate(2, "drain");

        await the.TheMessagesAreSent(1);
        await the.MessageHasStarted(1);
        await the.TheNodeRunningMessageStopsWhileGateOpens(1, "drain", 1500);

        await the.TheDurableInvariantsHold();
        the.NoMessageRanTwice();
    }

    [BobcatSpec]
    public async Task a_handler_outliving_the_drain_is_not_released_before_the_process_exits()
    {
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(1000);
        the.ListenersRunInParallel(2);
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(6);
        the.MessageWaitsForGate(1, "never");

        await the.TheMessagesAreSent(1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);
        await the.TheNodeRunningMessageShutsDownAndExits(1);

        await the.TheDurableInvariantsHold();
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    [BobcatSpec]
    public async Task a_killed_node_s_work_is_recovered_by_a_survivor()
    {
        the.SettleTimeout = 90.Seconds();
        the.TheClusterReactsWithinSeconds();
        the.ListenersRunInParallel(2);
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(12);
        the.MessageWaitsForGate(1, "never");

        await the.TheMessagesAreSent(1);
        await the.MessageHasStarted(1);
        await the.TheNodeRunningMessageIsKilled(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_handler_slower_than_the_inbox_stale_time_is_not_run_again()
    {
        the.TheClusterReactsWithinSeconds();
        the.TheInboxStaleTimeIs(2);
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(4);
        the.MessageIsSlow(1, 8000);

        await the.TheMessagesAreSent(1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);

        await the.TheDurableInvariantsHold();
        the.MessageRanOnce(1);
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    [BobcatSpec]
    public async Task a_backlog_older_than_the_inbox_stale_time_is_not_released()
    {
        the.TheClusterReactsWithinSeconds();
        the.TheInboxStaleTimeIs(2);
        the.ListenersRunInParallel(1);
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.MessagesToSend(8);
        for (var i = 1; i <= 8; i++) the.MessageIsSlow(i, 1500);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
        the.NoMessageRanTwice();
    }
}
