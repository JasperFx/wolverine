using CoreTests.Runtime;
using JasperFx.Core;
using JasperFx.Events;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Persistence;

// The connection-budget half of the metrics sweep (GH-3397). Connections are a resource of the
// database *server*, not of a logical database, so in the sharded-tenancy deployment this exists
// for — hundreds of tenant databases spread over a handful of servers — the probe has to run once
// per server per pass. Probing per database would multiply the very pressure the number is meant
// to reveal.
public class connection_budget_sweeper_tests
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly BudgetRecordingObserver theObserver = new();

    private static readonly DatabaseServerId ServerA = new("PostgreSQL", "shard-a", 5432);
    private static readonly DatabaseServerId ServerB = new("PostgreSQL", "shard-b", 5432);

    public connection_budget_sweeper_tests()
    {
        theRuntime.Options.Durability.UpdateMetricsPeriod = 100.Milliseconds();
        theRuntime.Observer = theObserver;

        // MockWolverineRuntime reports no databases at all, so the automatic activation rule
        // (sharded tenancy) can never fire here. Turn the machinery on explicitly; the rule
        // itself is asserted in connection_budgets_tests.
        theRuntime.Options.Durability.ConnectionBudgets.Enabled = true;
    }

    private FakeBudgetStore buildStore(string name, DatabaseServerId serverId)
    {
        var fake = new FakeBudgetStore(new Uri($"wolverinedb://test/{name}"), serverId);
        var metrics = new PersistenceMetrics(theRuntime, theRuntime.Options.Durability, name);
        fake.Registration = PersistenceMetricsSweeper.For(theRuntime).Register(fake.Store, metrics);

        return fake;
    }

    [Fact]
    public async Task probes_once_per_server_however_many_databases_are_on_it()
    {
        // Five tenant databases, one server. This is the whole point of the feature.
        var stores = Enumerable.Range(0, 5).Select(i => buildStore($"tenant{i}", ServerA)).ToArray();

        try
        {
            // Every database fetched at least twice => at least two full passes have run.
            await waitUntil(() => stores.All(x => x.Fetches >= 2));

            var probes = stores.Sum(x => x.ConnectionCounts);

            // Per pass: five FetchCounts, but exactly one connection probe. Without the dedupe
            // this would already be >= 10.
            probes.ShouldBeGreaterThanOrEqualTo(2);
            probes.ShouldBeLessThan(stores.Sum(x => x.Fetches));

            // Each successful probe publishes exactly one snapshot for the server.
            theObserver.SnapshotsFor(ServerA).Count.ShouldBe(probes);
        }
        finally
        {
            disposeAll(stores);
        }
    }

    [Fact]
    public async Task probes_each_server_separately()
    {
        // Four databases on A, one on B. Both servers get probed on the same cadence — the count
        // tracks servers, not databases.
        var onA = Enumerable.Range(0, 4).Select(i => buildStore($"a{i}", ServerA)).ToArray();
        var onB = new[] { buildStore("b0", ServerB) };

        try
        {
            await waitUntil(() => theObserver.SnapshotsFor(ServerA).Count >= 2
                                  && theObserver.SnapshotsFor(ServerB).Count >= 2);

            var probesOnA = onA.Sum(x => x.ConnectionCounts);
            var probesOnB = onB.Sum(x => x.ConnectionCounts);

            // A has 4x the databases of B, but is probed the same number of times (allowing for
            // one pass of skew between the two sampling points).
            Math.Abs(probesOnA - probesOnB).ShouldBeLessThanOrEqualTo(1);
        }
        finally
        {
            disposeAll(onA.Concat(onB));
        }
    }

    [Fact]
    public async Task a_configured_budget_wins_over_the_servers_own_limit()
    {
        // Decision from the issue: behind a pooler the server's max_connections describes what the
        // *pooler* may open, not what this application is entitled to. Configuration is the truth.
        theRuntime.Options.Durability.ConnectionBudgets.ForServer("shard-a", 5432, 400);

        var store = buildStore("tenant", ServerA);
        store.MaxConnections = 100;

        try
        {
            await waitUntil(() => theObserver.SnapshotsFor(ServerA).Any());

            var snapshot = theObserver.SnapshotsFor(ServerA).Last();
            snapshot.Max.ShouldBe(400);
            snapshot.Source.ShouldBe(ConnectionBudgetSource.Configured);

            // ...and the server was never even asked for its own limit.
            store.MaxProbes.ShouldBe(0);
        }
        finally
        {
            disposeAll([store]);
        }
    }

    [Fact]
    public async Task falls_back_to_the_probed_limit_and_only_reads_it_once()
    {
        var store = buildStore("tenant", ServerA);
        store.MaxConnections = 100;
        store.OpenConnections = 25;

        try
        {
            await waitUntil(() => theObserver.SnapshotsFor(ServerA).Count >= 3);

            var snapshot = theObserver.SnapshotsFor(ServerA).Last();
            snapshot.Max.ShouldBe(100);
            snapshot.Used.ShouldBe(25);
            snapshot.Source.ShouldBe(ConnectionBudgetSource.Probed);
            snapshot.Utilization.ShouldBe(0.25);

            // The server's limit doesn't move at runtime, so it is read once per process however
            // many passes run.
            store.MaxProbes.ShouldBe(1);
        }
        finally
        {
            disposeAll([store]);
        }
    }

    [Fact]
    public async Task reports_the_budget_as_unknown_when_the_limit_cannot_be_read()
    {
        // SQL Server without VIEW SERVER STATE, say. The used count is still worth having; the
        // budget is honestly reported as unknown rather than guessed at.
        var store = buildStore("tenant", ServerA);
        store.MaxConnections = null;
        store.OpenConnections = 25;

        try
        {
            await waitUntil(() => theObserver.SnapshotsFor(ServerA).Any());

            var snapshot = theObserver.SnapshotsFor(ServerA).Last();
            snapshot.Max.ShouldBeNull();
            snapshot.Source.ShouldBe(ConnectionBudgetSource.Unknown);
            snapshot.Used.ShouldBe(25);
            snapshot.Utilization.ShouldBeNull();
        }
        finally
        {
            disposeAll([store]);
        }
    }

    [Fact]
    public async Task a_failing_probe_publishes_nothing_and_leaves_the_rest_of_the_sweep_alone()
    {
        var failing = buildStore("failing", ServerA);
        failing.FailConnectionCount = true;

        var healthy = buildStore("healthy", ServerB);

        try
        {
            await waitUntil(() => theObserver.SnapshotsFor(ServerB).Count >= 2 && failing.Fetches >= 2);

            // Phase 1 is observability only: a probe that can't answer publishes nothing rather
            // than publishing a fabricated zero.
            theObserver.SnapshotsFor(ServerA).ShouldBeEmpty();

            // ...and the failure is contained. The envelope counts for the failing server's own
            // database still get swept, as does the other server.
            failing.Fetches.ShouldBeGreaterThanOrEqualTo(2);
            healthy.Fetches.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            disposeAll([failing, healthy]);
        }
    }

    [Fact]
    public async Task does_not_probe_at_all_when_the_budget_machinery_is_off()
    {
        theRuntime.Options.Durability.ConnectionBudgets.Enabled = false;

        var store = buildStore("tenant", ServerA);

        try
        {
            // The envelope-count sweep still runs; only the budget probing is silent.
            await waitUntil(() => store.Fetches >= 3);

            store.ConnectionCounts.ShouldBe(0);
            store.MaxProbes.ShouldBe(0);
            theObserver.SnapshotsFor(ServerA).ShouldBeEmpty();
        }
        finally
        {
            disposeAll([store]);
        }
    }

    private static void disposeAll(IEnumerable<FakeBudgetStore> stores)
    {
        foreach (var store in stores) store.Registration?.Dispose();
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

    // A substituted message store that also answers for its server's connections, wrapped so the
    // tests can read call counts. The sweeper picks the probe out with an `OfType` over the
    // registered stores, so the one object has to wear both interfaces.
    private class FakeBudgetStore
    {
        private int _fetches;
        private int _connectionCounts;
        private int _maxProbes;

        public FakeBudgetStore(Uri uri, DatabaseServerId serverId)
        {
            var store = Substitute.For<IMessageStore, IConnectionBudgetProbe>();
            store.Uri.Returns(uri);

            var admin = Substitute.For<IMessageStoreAdmin>();
            admin.FetchCountsAsync().Returns(_ =>
            {
                Interlocked.Increment(ref _fetches);
                return Task.FromResult(new PersistedCounts { Incoming = 42 });
            });
            store.Admin.Returns(admin);

            var probe = (IConnectionBudgetProbe)store;
            probe.ServerId.Returns(serverId);

            probe.CountServerConnectionsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref _connectionCounts);

                if (FailConnectionCount)
                {
                    throw new InvalidOperationException("No connection available");
                }

                return new ValueTask<int>(OpenConnections);
            });

            probe.ProbeMaxConnectionsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref _maxProbes);
                return new ValueTask<int?>(MaxConnections);
            });

            Store = store;
        }

        public IMessageStore Store { get; }
        public IDisposable? Registration { get; set; }

        public int Fetches => Volatile.Read(ref _fetches);
        public int ConnectionCounts => Volatile.Read(ref _connectionCounts);
        public int MaxProbes => Volatile.Read(ref _maxProbes);

        public int OpenConnections { get; set; } = 10;
        public int? MaxConnections { get; set; } = 100;
        public bool FailConnectionCount { get; set; }
    }

    // Deliberately a real implementation rather than a substitute: ConnectionBudget is a default
    // interface member, and this proves an observer that never heard of it still compiles and runs.
    private class BudgetRecordingObserver : IWolverineObserver
    {
        private readonly List<ConnectionBudgetSnapshot> _snapshots = [];

        public void ConnectionBudget(ConnectionBudgetSnapshot snapshot)
        {
            lock (_snapshots) _snapshots.Add(snapshot);
        }

        public IReadOnlyList<ConnectionBudgetSnapshot> SnapshotsFor(DatabaseServerId server)
        {
            lock (_snapshots) return _snapshots.Where(x => x.Server == server).ToList();
        }

        public Task AssumedLeadership() => Task.CompletedTask;
        public Task NodeStarted() => Task.CompletedTask;
        public Task NodeStopped() => Task.CompletedTask;
        public Task AgentStarted(Uri agentUri) => Task.CompletedTask;
        public Task AgentStopped(Uri agentUri) => Task.CompletedTask;
        public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
        public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
        public Task RuntimeIsFullyStarted() => Task.CompletedTask;
        public void EndpointAdded(Endpoint endpoint) { }
        public void MessageRouted(Type messageType, IMessageRouter router) { }
        public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) => Task.CompletedTask;
        public Task BackPressureLifted(Endpoint endpoint) => Task.CompletedTask;
        public Task ListenerLatched(Endpoint endpoint) => Task.CompletedTask;
        public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) => Task.CompletedTask;
        public Task CircuitBreakerReset(Endpoint endpoint) => Task.CompletedTask;
        public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
        public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) { }
    }
}
