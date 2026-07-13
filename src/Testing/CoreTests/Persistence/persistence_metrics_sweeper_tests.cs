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

    // Scoped per test, deliberately NOT static: a sweeper loop started by an earlier test
    // keeps running (nothing cancels MockWolverineRuntime), so cross-test static counters
    // would let a stale loop's in-flight fetch inflate this test's observed concurrency.
    private readonly ConcurrencyTracker theTracker = new();

    public persistence_metrics_sweeper_tests()
    {
        // keep passes short so the tests observe multiple sweeps quickly; the jittered
        // start delays the first pass by at most one period
        theRuntime.Options.Durability.UpdateMetricsPeriod = 100.Milliseconds();
    }

    private (IMessageStore store, PersistenceMetrics metrics, ConcurrencyTrackingAdmin admin) buildStore(string name)
    {
        var admin = new ConcurrencyTrackingAdmin(theTracker);
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
    public async Task a_replacement_registration_for_the_same_database_survives_the_old_agents_dispose()
    {
        var sweeper = PersistenceMetricsSweeper.For(theRuntime);

        // Same database URI, two successive agents — an agent for this database is stopping on
        // this node while a replacement for it starts (agent redistribution). Both admins are
        // distinct instances, so we can tell which registration the sweep is actually polling.
        var stopping = buildStore("shared");
        var replacement = buildStore("shared");
        stopping.store.Uri.ShouldBe(replacement.store.Uri);

        var stoppingRegistration = sweeper.Register(stopping.store, stopping.metrics);
        var replacementRegistration = sweeper.Register(replacement.store, replacement.metrics);

        try
        {
            // The stopping agent disposes AFTER the replacement registered. Removing by URI
            // alone would evict the live registration and silently stop polling the database.
            stoppingRegistration.Dispose();

            await waitUntil(() => replacement.admin.Fetches >= 2);
            replacement.metrics.Counts.Incoming.ShouldBe(ConcurrencyTrackingAdmin.TheCounts.Incoming);
        }
        finally
        {
            replacementRegistration.Dispose();
        }
    }

    [Fact]
    public void a_non_positive_update_metrics_period_is_rejected()
    {
        // A zero/negative period would hot-spin the sweep loop rather than fail, so it is
        // rejected at configuration time. DurabilityMetricsEnabled is the way to turn it off.
        Should.Throw<ArgumentOutOfRangeException>(() =>
            theRuntime.Options.Durability.UpdateMetricsPeriod = TimeSpan.Zero);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            theRuntime.Options.Durability.UpdateMetricsPeriod = -1.Seconds());
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

    // Concurrent + peak FetchCountsAsync counts shared by every admin built for ONE test,
    // so the sequential guarantee can be asserted without leaking across tests.
    private class ConcurrencyTracker
    {
        private int _inFlight;
        private int _maxObserved;

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        public IDisposable Enter()
        {
            var current = Interlocked.Increment(ref _inFlight);
            interlockedMax(ref _maxObserved, current);
            return new Exit(this);
        }

        private static void interlockedMax(ref int location, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref location)))
            {
                Interlocked.CompareExchange(ref location, value, current);
            }
        }

        private sealed class Exit(ConcurrencyTracker parent) : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref parent._inFlight);
        }
    }

    // Counts total FetchCountsAsync calls for one store and feeds the test's shared
    // concurrency tracker; everything else is unused by the sweeper.
    private class ConcurrencyTrackingAdmin(ConcurrencyTracker tracker) : IMessageStoreAdmin
    {
        public static readonly PersistedCounts TheCounts = new() { Incoming = 42, Scheduled = 3, Outgoing = 7 };

        public int Fetches;
        public int MaxObservedConcurrency => tracker.MaxObservedConcurrency;
        public TimeSpan FetchDelay = TimeSpan.Zero;

        public async Task<PersistedCounts> FetchCountsAsync()
        {
            using var _ = tracker.Enter();

            if (FetchDelay > TimeSpan.Zero)
            {
                await Task.Delay(FetchDelay);
            }

            Interlocked.Increment(ref Fetches);
            return TheCounts;
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
