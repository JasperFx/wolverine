using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// Publishes the per-server connection budget as OpenTelemetry gauges. One instrument pair covers
/// every server, sliced by the <c>server</c> tag, so a deployment that spans shards with different
/// budgets stays queryable as one series. See wolverine#3397.
/// </summary>
internal class ConnectionBudgetMetrics
{
    private readonly ConcurrentDictionary<DatabaseServerId, ConnectionBudgetSnapshot> _snapshots = new();
    private readonly string _serviceName;

    // Held so the meter keeps the instruments alive for the life of the runtime; the callbacks are
    // what actually do the work on each export.
    private readonly ObservableGauge<int> _used;
    private readonly ObservableGauge<int> _budget;

    public ConnectionBudgetMetrics(IWolverineRuntime runtime)
    {
        _serviceName = runtime.Options.ServiceName;

        _used = runtime.Meter.CreateObservableGauge(MetricsConstants.DatabaseConnectionCount,
            observeUsed, MetricsConstants.Connections, "Connections currently open on the database server");

        _budget = runtime.Meter.CreateObservableGauge(MetricsConstants.DatabaseConnectionBudget,
            observeBudget, MetricsConstants.Connections, "Connection budget for the database server");
    }

    public void Update(ConnectionBudgetSnapshot snapshot)
    {
        _snapshots[snapshot.Server] = snapshot;
    }

    private IEnumerable<Measurement<int>> observeUsed()
    {
        foreach (var snapshot in _snapshots.Values)
        {
            yield return new Measurement<int>(snapshot.Used, tagsFor(snapshot.Server));
        }
    }

    private IEnumerable<Measurement<int>> observeBudget()
    {
        foreach (var snapshot in _snapshots.Values)
        {
            // A server whose budget is Unknown emits no budget measurement at all. Emitting a zero
            // or a sentinel would be worse than silence: a dashboard would happily chart it as a
            // real limit and compute a utilization of infinity against it.
            if (snapshot.Max is null) continue;

            yield return new Measurement<int>(snapshot.Max.Value, tagsFor(snapshot.Server));
        }
    }

    private KeyValuePair<string, object?>[] tagsFor(DatabaseServerId server)
    {
        return
        [
            new KeyValuePair<string, object?>(MetricsConstants.SourceKey, _serviceName),
            new KeyValuePair<string, object?>(MetricsConstants.ServerKey, server.ToString())
        ];
    }
}
