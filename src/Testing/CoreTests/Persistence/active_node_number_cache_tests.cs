using CoreTests.Runtime;
using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Persistence;

// The durability agent's recovery timer needs the active node numbers, but the call that yields
// them also selects the whole assignment table, and there is one durability agent per message
// database. On a sharded fleet that turned a per-node fact into a per-database query. These tests
// pin the collapsing: one fetch serves every database on the node until the polling interval is up.
public class active_node_number_cache_tests
{
    private readonly MockWolverineRuntime theRuntime = new();

    private ActiveNodeNumberCache theCache => new(theRuntime);

    public active_node_number_cache_tests()
    {
        theRuntime.DurabilitySettings.ScheduledJobPollingTime = 200.Milliseconds();
        nodesAre(1, 2, 3);
    }

    private void nodesAre(params int[] numbers)
    {
        theRuntime.Storage.Nodes
            .LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns(numbers.Select(x => new WolverineNode { AssignedNodeNumber = x }).ToList());
    }

    private int fetchCount => theRuntime.Storage.Nodes.ReceivedCalls()
        .Count(x => x.GetMethodInfo().Name == nameof(INodeAgentPersistence.LoadAllNodesAsync));

    [Fact]
    public async Task returns_the_assigned_node_numbers()
    {
        var numbers = await theCache.FetchAsync(CancellationToken.None);
        numbers.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task one_fetch_serves_every_database_on_the_node()
    {
        var cache = theCache;

        // stand in for the node's message databases all polling on the same timer tick
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => cache.FetchAsync(CancellationToken.None).AsTask()));

        fetchCount.ShouldBe(1);
        results.ShouldAllBe(x => x.SequenceEqual(new[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task refetches_once_the_polling_interval_has_passed()
    {
        var cache = theCache;
        await cache.FetchAsync(CancellationToken.None);

        nodesAre(4, 5);
        await Task.Delay(theRuntime.DurabilitySettings.ScheduledJobPollingTime + 100.Milliseconds(),
            TestContext.Current.CancellationToken);

        var numbers = await cache.FetchAsync(CancellationToken.None);

        numbers.ShouldBe([4, 5]);
        fetchCount.ShouldBe(2);
    }

    [Fact]
    public async Task a_failed_lookup_is_not_cached_and_reaches_the_caller()
    {
        var cache = theCache;
        theRuntime.Storage.Nodes
            .LoadAllNodesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WolverineNode>>(_ => throw new TimeoutException("pool exhausted"));

        // the durability agent has its own try/catch and decides what a failed lookup means, so the
        // cache must not swallow it — nor remember an empty result as if it were the truth
        await Should.ThrowAsync<TimeoutException>(() => cache.FetchAsync(CancellationToken.None).AsTask());

        nodesAre(7);
        var numbers = await cache.FetchAsync(CancellationToken.None);
        numbers.ShouldBe([7]);
    }

    [Fact]
    public void one_cache_per_runtime()
    {
        ActiveNodeNumberCache.For(theRuntime).ShouldBeSameAs(ActiveNodeNumberCache.For(theRuntime));
        ActiveNodeNumberCache.For(new MockWolverineRuntime())
            .ShouldNotBeSameAs(ActiveNodeNumberCache.For(theRuntime));
    }

    // ─── GH-3850: the staleness window, stated rather than implied ───────────────────────────

    [Fact]
    public async Task the_high_water_mark_is_the_highest_number_seen()
    {
        var cache = theCache;
        await cache.FetchAsync(CancellationToken.None);

        cache.HighWaterMark.ShouldBe(3);
    }

    [Fact]
    public async Task the_high_water_mark_does_not_drop_when_the_highest_node_departs()
    {
        var cache = theCache;
        await cache.FetchAsync(CancellationToken.None);

        // node 3 -- the highest -- dies
        nodesAre(1, 2);
        await Task.Delay(theRuntime.DurabilitySettings.ScheduledJobPollingTime + 100.Milliseconds(),
            TestContext.Current.CancellationToken);
        await cache.FetchAsync(CancellationToken.None);

        // max(active) would now be 2, which would put node 3's orphaned messages permanently out of
        // reach of the release. The mark is monotonic precisely so that cannot happen.
        cache.HighWaterMark.ShouldBe(3);
    }

    [Fact]
    public async Task the_high_water_mark_rises_for_a_node_that_joins()
    {
        var cache = theCache;
        await cache.FetchAsync(CancellationToken.None);

        nodesAre(1, 2, 3, 9);
        await Task.Delay(theRuntime.DurabilitySettings.ScheduledJobPollingTime + 100.Milliseconds(),
            TestContext.Current.CancellationToken);
        await cache.FetchAsync(CancellationToken.None);

        cache.HighWaterMark.ShouldBe(9);
    }

    [Fact]
    public async Task a_node_that_joins_mid_interval_is_above_the_mark_and_so_is_not_judged()
    {
        var cache = theCache;
        await cache.FetchAsync(CancellationToken.None);

        // Node 4 registers after the fetch. It is absent from the cached list, so the release would
        // reset its in-flight rows to owner 0 and let another node claim work it is already doing --
        // the one direction of staleness that is not benign. Its number is above the mark, which is
        // what keeps the release away from it until the next fetch sees it.
        const int joinedAfterTheFetch = 4;

        var numbers = await cache.FetchAsync(CancellationToken.None);
        numbers.ShouldNotContain(joinedAfterTheFetch);
        joinedAfterTheFetch.ShouldBeGreaterThan(cache.HighWaterMark);
    }
}
