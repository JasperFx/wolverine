using System.Collections.Concurrent;
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

    private PersistenceMetricsSweeper(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<PersistenceMetricsSweeper>();
    }

    private sealed record Registration(IMessageStore Store, PersistenceMetrics Metrics);

    /// <summary>
    /// Add a store to the node's metrics sweep. Dispose the returned handle to remove it
    /// again — durability agents do this when they stop, so a database that moves to
    /// another node stops being polled from this one.
    /// </summary>
    public IDisposable Register(IMessageStore store, PersistenceMetrics metrics)
    {
        _registrations[store.Uri] = new Registration(store, metrics);
        ensureStarted();
        return new Unregistration(this, store.Uri);
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

            // One database at a time, spaced so the pass fills the UpdateMetricsPeriod window:
            // at most one metrics query (and its pooled connection) is in flight per node.
            var spacing = period / pass.Length;
            foreach (var registration in pass)
            {
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

                await Task.Delay(spacing, cancellation).ConfigureAwait(false);
            }
        }
    }

    private sealed class Unregistration : IDisposable
    {
        private readonly PersistenceMetricsSweeper _parent;
        private readonly Uri _uri;

        public Unregistration(PersistenceMetricsSweeper parent, Uri uri)
        {
            _parent = parent;
            _uri = uri;
        }

        public void Dispose()
        {
            _parent._registrations.TryRemove(_uri, out _);
        }
    }
}
