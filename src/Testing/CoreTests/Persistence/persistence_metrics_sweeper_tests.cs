using CoreTests.Runtime;
using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

// Unit tests for the node-wide sequential metrics sweeper added for GH-3375. Durability
// agents register their store instead of running a per-database PeriodicTimer; the sweeper
// walks the node's registered databases one at a time across the UpdateMetricsPeriod window
// and re-reads the registration set every pass, so databases whose agents start or stop
// join and leave the sweep without a restart.
public class persistence_metrics_sweeper_tests
{
    private readonly MockWolverineRuntime theRuntime = new();

    public persistence_metrics_sweeper_tests()
    {
        // keep passes short so the tests observe multiple sweeps quickly; the jittered
        // start delays the first pass by at most one period
        theRuntime.Options.Durability.UpdateMetricsPeriod = 100.Milliseconds();
    }

    private (IMessageStore store, PersistenceMetrics metrics, ConcurrencyTrackingAdmin admin) buildStore(string name)
    {
        var admin = new ConcurrencyTrackingAdmin();
        var store = Substitute.For<IMessageStore>();
        store.Uri.Returns(new Uri($"wolverinedb://test/{name}"));
        store.Admin.Returns(admin);

        var metrics = new PersistenceMetrics(theRuntime, theRuntime.Options.Durability, name);
        return (store, metrics, admin);
    }

    [Fact]
    public async Task sweeps_every_registered_store_and_publishes_counts()
    {
        var sweeper = PersistenceMetricsSweeper.For(theRuntime);

        var stores = new[] { buildStore("one"), buildStore("two"), buildStore("three") };
        var registrations = stores.Select(x => sweeper.Register(x.store, x.metrics)).ToArray();

        try
        {
            await waitUntil(() => stores.All(x => x.admin.Fetches > 0));

            foreach (var (store, metrics, _) in stores)
            {
                metrics.Counts.Incoming.ShouldBe(ConcurrencyTrackingAdmin.TheCounts.Incoming);
                theRuntime.Observer.Received().PersistedCounts(store.Uri, Arg.Any<PersistedCounts>());
            }
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    [Fact]
    public async Task a_disposed_registration_leaves_the_sweep()
    {
        var sweeper = PersistenceMetricsSweeper.For(theRuntime);

        var staying = buildStore("staying");
        var leaving = buildStore("leaving");

        var stayingRegistration = sweeper.Register(staying.store, staying.metrics);
        var leavingRegistration = sweeper.Register(leaving.store, leaving.metrics);

        try
        {
            await waitUntil(() => leaving.admin.Fetches > 0 && staying.admin.Fetches > 0);

            leavingRegistration.Dispose();
            var fetchesAtRemoval = leaving.admin.Fetches;
            var stayingFetches = staying.admin.Fetches;

            // the set is re-read per pass, so after a couple more passes for the store that
            // stayed, the removed store must not have been polled again
            await waitUntil(() => staying.admin.Fetches >= stayingFetches + 2);
            leaving.admin.Fetches.ShouldBe(fetchesAtRemoval);
        }
        finally
        {
            stayingRegistration.Dispose();
        }
    }

    [Fact]
    public async Task polls_one_store_at_a_time()
    {
        var sweeper = PersistenceMetricsSweeper.For(theRuntime);

        // slow fetches would overlap under per-store timers; the sequential sweep may not
        // ever have more than one in flight
        var stores = Enumerable.Range(0, 4).Select(i => buildStore($"seq{i}")).ToArray();
        foreach (var (_, _, admin) in stores) admin.FetchDelay = 30.Milliseconds();

        var registrations = stores.Select(x => sweeper.Register(x.store, x.metrics)).ToArray();

        try
        {
            await waitUntil(() => stores.All(x => x.admin.Fetches >= 2));
            stores.Max(x => x.admin.MaxObservedConcurrency).ShouldBe(1);
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    private static async Task waitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "timed out waiting for the sweeper");
            await Task.Delay(25);
        }
    }

    // Counts concurrent + total FetchCountsAsync calls across ALL instances so the
    // sequential guarantee can be asserted; everything else is unused by the sweeper.
    private class ConcurrencyTrackingAdmin : IMessageStoreAdmin
    {
        public static readonly PersistedCounts TheCounts = new() { Incoming = 42, Scheduled = 3, Outgoing = 7 };

        private static int _inFlight;
        private static int _maxObserved;

        public int Fetches;
        public int MaxObservedConcurrency => _maxObserved;
        public TimeSpan FetchDelay = TimeSpan.Zero;

        public async Task<PersistedCounts> FetchCountsAsync()
        {
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxObserved, current);

            try
            {
                if (FetchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(FetchDelay);
                }

                Interlocked.Increment(ref Fetches);
                return TheCounts;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref location)))
            {
                Interlocked.CompareExchange(ref location, value, current);
            }
        }

        public Task DeleteAllHandledAsync() => Task.CompletedTask;

        public Task ClearAllAsync() => Task.CompletedTask;

        public Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType) => Task.FromResult(0);

        public Task RebuildAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<Envelope>> AllIncomingAsync() => Task.FromResult((IReadOnlyList<Envelope>)[]);

        public Task<IReadOnlyList<Envelope>> AllOutgoingAsync() => Task.FromResult((IReadOnlyList<Envelope>)[]);

        public Task ReleaseAllOwnershipAsync() => Task.CompletedTask;

        public Task ReleaseAllOwnershipAsync(int ownerId) => Task.CompletedTask;

        public Task CheckConnectivityAsync(CancellationToken token) => Task.CompletedTask;

        public Task MigrateAsync() => Task.CompletedTask;
    }
}
