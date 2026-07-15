using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// Node-wide sequential sweeper for the durability metrics (GH-3375). Instead of every
/// durability agent running its own in-phase <c>PeriodicTimer</c> — which pins a pooled
/// connection per database per node and, at high database counts, turns the metrics
/// polling itself into significant connection pressure — each agent registers its store
/// here and a single loop walks the node's registered databases sequentially, spreading
/// the <see cref="DurabilitySettings.UpdateMetricsPeriod"/> window across them. That
/// bounds the simultaneous metrics connections to one per node regardless of database
/// count. The registration set is re-read every pass, so databases whose agents start or
/// stop (e.g. agent redistribution) join and leave the sweep without a restart.
/// </summary>
public class PersistenceMetricsSweeper
{
    private static readonly ConditionalWeakTable<IWolverineRuntime, PersistenceMetricsSweeper> _perRuntime = new();

    public static PersistenceMetricsSweeper For(IWolverineRuntime runtime)
    {
        return _perRuntime.GetValue(runtime, r => new PersistenceMetricsSweeper(r));
    }

    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<PersistenceMetricsSweeper> _logger;
    private readonly ConcurrentDictionary<Uri, Registration> _registrations = new();
    private readonly object _startLock = new();
    private Task? _loop;

    // Connection-budget state (#3397). Only ever touched from the single sweep loop, so a plain
    // Dictionary is right — no concurrency to defend against.
    private readonly Dictionary<DatabaseServerId, ServerBudgetState> _servers = new();
    private ConnectionBudgetMetrics? _budgetMetrics;

    private PersistenceMetricsSweeper(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<PersistenceMetricsSweeper>();
    }

    private sealed class ServerBudgetState
    {
        /// <summary>The server's own limit, read at most once per process — it doesn't move at runtime.</summary>
        public int? ProbedMax { get; set; }

        public bool HasProbedMax { get; set; }

        /// <summary>Latches the "couldn't count connections" log so a persistently failing probe doesn't flood.</summary>
        public bool CountFailureLogged { get; set; }
    }

    // A class, not a record: unregistration removes the exact registration *instance* it
    // created, and that identity check relies on reference equality. A record's value
    // equality would let a stale agent's Dispose match — and drop — the live registration
    // that replaced it for the same database.
    private sealed class Registration(IMessageStore store, PersistenceMetrics metrics)
    {
        public IMessageStore Store { get; } = store;
        public PersistenceMetrics Metrics { get; } = metrics;
    }

    /// <summary>
    /// Add a store to the node's metrics sweep. Dispose the returned handle to remove it
    /// again — durability agents do this when they stop, so a database that moves to
    /// another node stops being polled from this one.
    /// </summary>
    public IDisposable Register(IMessageStore store, PersistenceMetrics metrics)
    {
        var registration = new Registration(store, metrics);
        _registrations[store.Uri] = registration;
        ensureStarted();
        return new Unregistration(this, store.Uri, registration);
    }

    private void ensureStarted()
    {
        if (_loop is not null)
        {
            return;
        }

        lock (_startLock)
        {
            _loop ??= Task.Run(() => sweepAsync(_runtime.Cancellation), _runtime.Cancellation);
        }
    }

    private async Task sweepAsync(CancellationToken cancellation)
    {
        try
        {
            // Jittered start (mirrors ScheduledJobFirstExecution) so multiple nodes don't
            // sweep in phase against the same database server.
            var period = _runtime.Options.Durability.UpdateMetricsPeriod;
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * period.TotalMilliseconds);
            await Task.Delay(jitter, cancellation).ConfigureAwait(false);

            await runPassesAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // node shutting down
        }
    }

    private async Task runPassesAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var period = _runtime.Options.Durability.UpdateMetricsPeriod;
            // Re-read the registration set every pass: agents come and go with ownership
            // changes, and the set must reflect what this node currently runs.
            var pass = _registrations.Values.ToArray();

            if (pass.Length == 0)
            {
                await Task.Delay(period, cancellation).ConfigureAwait(false);
                continue;
            }

            // Connection budgets first, and deliberately outside the deadline window below: this is
            // one query per *server*, not per database, so it costs a fixed handful of queries no
            // matter how many tenant databases this node owns. Doing it before passStart leaves the
            // database pacing exactly as it was.
            await probeConnectionBudgetsAsync(pass, cancellation).ConfigureAwait(false);

            // One database at a time, paced against deadlines so the pass fills the
            // UpdateMetricsPeriod window: at most one metrics query (and its pooled
            // connection) is in flight per node. Each store's next-poll target is
            // passStart + (i + 1) * (period / count), so fetch time is absorbed into the
            // spacing rather than stretching the effective polling interval, and the last
            // target IS the end of the pass — no extra idle delay on top of the period.
            var spacing = period / pass.Length;
            var passStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < pass.Length; i++)
            {
                var registration = pass[i];
                try
                {
                    var counts = await registration.Store.Admin.FetchCountsAsync().ConfigureAwait(false);
                    registration.Metrics.Counts = counts;
                    _runtime.Observer.PersistedCounts(registration.Store.Uri, counts);
                }
                catch (OperationCanceledException)
                {
                    // shutting down; the loop condition handles it
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to update the metrics on envelope storage for {Store}",
                        registration.Store.Uri);
                }

                var remaining = spacing * (i + 1) - Stopwatch.GetElapsedTime(passStart);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellation).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Probe each distinct database server behind this pass's registrations exactly once and publish
    /// its connection budget. The dedupe is the entire point: in the sharded-tenancy deployment this
    /// exists for (#3397), a node can own hundreds of tenant databases that all live on a handful of
    /// servers, and connections are a resource of the server. Probing per database would multiply
    /// the very pressure the number is meant to reveal.
    /// </summary>
    private async Task probeConnectionBudgetsAsync(Registration[] pass, CancellationToken cancellation)
    {
        var budgets = _runtime.Options.Durability.ConnectionBudgets;
        if (!budgets.IsActive(_runtime.Stores.Cardinality()))
        {
            return;
        }

        // Distinct servers, not distinct stores. A provider with no cheap server-wide connection
        // count simply doesn't implement the probe and drops out here.
        var probes = pass
            .Select(x => x.Store)
            .OfType<IConnectionBudgetProbe>()
            .GroupBy(x => x.ServerId)
            .ToArray();

        if (probes.Length == 0)
        {
            return;
        }

        _budgetMetrics ??= new ConnectionBudgetMetrics(_runtime);

        foreach (var group in probes)
        {
            cancellation.ThrowIfCancellationRequested();

            var serverId = group.Key;

            // Any one of the stores on this server can answer for it; they share the connection
            // pool's destination. Take the first — if it's unhealthy the whole server is suspect
            // anyway, and that's a signal, not an accident.
            var probe = group.First();

            if (!_servers.TryGetValue(serverId, out var state))
            {
                state = new ServerBudgetState();
                _servers[serverId] = state;
            }

            int used;
            try
            {
                used = await probe.CountServerConnectionsAsync(cancellation).ConfigureAwait(false);
                state.CountFailureLogged = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // Phase 1 is observability only, so a failed probe publishes nothing and the sweep
                // carries on. (The adaptive phase will read repeated failure here as *maximal*
                // pressure rather than missing data — a probe that cannot get a connection is the
                // strongest signal there is. Deliberately not acted on yet.)
                if (!state.CountFailureLogged)
                {
                    state.CountFailureLogged = true;
                    _logger.LogWarning(e,
                        "Unable to count the open connections on database server {Server}. The connection budget for this server will not be reported until the probe succeeds.",
                        serverId);
                }

                continue;
            }

            var (max, source) = await resolveBudgetAsync(serverId, probe, state, budgets, cancellation)
                .ConfigureAwait(false);

            var snapshot = new ConnectionBudgetSnapshot(serverId, used, max, source);

            _budgetMetrics.Update(snapshot);

            try
            {
                _runtime.Observer.ConnectionBudget(snapshot);
            }
            catch (Exception e)
            {
                // A misbehaving observer must not take down the metrics sweep for every database
                // on the node.
                _logger.LogError(e, "Error publishing the connection budget for database server {Server}", serverId);
            }
        }
    }

    private async ValueTask<(int? Max, ConnectionBudgetSource Source)> resolveBudgetAsync(
        DatabaseServerId serverId,
        IConnectionBudgetProbe probe,
        ServerBudgetState state,
        ConnectionBudgets budgets,
        CancellationToken cancellation)
    {
        // Configuration wins, always. Behind a pooler the server's own max_connections describes
        // what the *pooler* may open, not what this application is entitled to — so a declared
        // budget is the only one that means anything in that topology.
        var configured = budgets.MaxFor(serverId);
        if (configured.HasValue)
        {
            return (configured.Value, ConnectionBudgetSource.Configured);
        }

        if (!state.HasProbedMax)
        {
            state.HasProbedMax = true;

            try
            {
                state.ProbedMax = await probe.ProbeMaxConnectionsAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Leave HasProbedMax latched false-ish for the next pass rather than caching a
                // shutdown as a permanent "unknown".
                state.HasProbedMax = false;
                throw;
            }
            catch (Exception e)
            {
                state.ProbedMax = null;
                _logger.LogWarning(e,
                    "Unable to read the connection limit from database server {Server}. Reporting its connection budget as unknown. Declare one explicitly with Durability.ConnectionBudgets.ForServer(...) to get a utilization reading.",
                    serverId);
            }
        }

        return state.ProbedMax.HasValue
            ? (state.ProbedMax, ConnectionBudgetSource.Probed)
            : (null, ConnectionBudgetSource.Unknown);
    }

    private sealed class Unregistration : IDisposable
    {
        private readonly PersistenceMetricsSweeper _parent;
        private readonly Uri _uri;
        private readonly Registration _registration;

        public Unregistration(PersistenceMetricsSweeper parent, Uri uri, Registration registration)
        {
            _parent = parent;
            _uri = uri;
            _registration = registration;
        }

        public void Dispose()
        {
            // Remove only if this exact registration is still the live one. A blind
            // remove-by-URI would silently evict a newer agent's registration for the same
            // database when a stopping agent's Dispose interleaves after the replacement
            // registers — and that database would then go unpolled until the next restart.
            _parent._registrations.TryRemove(
                new KeyValuePair<Uri, Registration>(_uri, _registration));
        }
    }
}
