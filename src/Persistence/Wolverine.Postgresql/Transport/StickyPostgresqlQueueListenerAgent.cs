using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport;

internal class StickyPostgresqlQueueListenerAgent : IAgent
{
    // Per-tenant DB failures stay Degraded for the first few cycles and only escalate to
    // Unhealthy once they persist. Mirrors the consecutive-failure semantics in
    // DurabilityHealthSignals (#2646). Hard-coded here rather than pulling from
    // DurabilitySettings so #2647 doesn't depend on #2646 landing first.
    private const int ConsecutiveDbFailureUnhealthyThreshold = 3;

    private readonly IWolverineRuntime _runtime;
    private readonly string _queue;
    private readonly string _databaseName;
    private TenantedPostgresqlQueue? _tenantEndpoint;
    private int _consecutiveDbFailures;

    public StickyPostgresqlQueueListenerAgent(IWolverineRuntime runtime, string queue, string databaseName)
    {
        _runtime = runtime;
        _queue = queue;
        _databaseName = databaseName;

        Uri = new Uri($"{StickyPostgresqlQueueListenerAgentFamily.StickyListenerSchema}://{_queue}/{_databaseName}");
    }

    public AgentStatus Status { get; set; } = AgentStatus.Running;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _tenantEndpoint ??= await findOrBuildEndpoint();

        await _runtime.Endpoints.StartListenerAsync(_tenantEndpoint, cancellationToken);
    }

    private async Task<TenantedPostgresqlQueue> findOrBuildEndpoint()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();

        if (transport.Databases == null)
            throw new InvalidOperationException("This system is not using multi-tenancy by database");

        var queue = transport.Queues[_queue];

        var database = (PostgresqlMessageStore)await transport.Databases.GetDatabaseAsync(_databaseName);

        var tenantEndpoint = new TenantedPostgresqlQueue(queue, database.NpgsqlDataSource, _databaseName);
        return tenantEndpoint;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _tenantEndpoint ??= await findOrBuildEndpoint();
        await _runtime.Endpoints.StopListenerAsync(_tenantEndpoint, cancellationToken);
        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; }

    /// <summary>
    /// Human-readable description for monitoring tools — see
    /// <see cref="IAgent.Description"/>.
    /// </summary>
    public string Description => $"Sticky Postgres queue listener — pinned to the per-tenant database '{_databaseName}' for queue '{_queue}'. Only one node listens to each tenant database to avoid duplicate consumption.";

    /// <summary>
    /// Per-tenant health-check enrichments for the sticky Postgres queue listener (see #2647).
    /// Layers three signals on top of the agent <see cref="Status"/>:
    ///
    /// <list type="number">
    ///   <item><b>Per-tenant database reachability</b> — a `SELECT 1` against the assigned
    ///   <see cref="NpgsqlDataSource"/>. One failure ⇒ Degraded; <see cref="ConsecutiveDbFailureUnhealthyThreshold"/>
    ///   consecutive failures ⇒ Unhealthy. The underlying error is surfaced as the
    ///   description so operators see the specific node + tenant pair that's misbehaving.</item>
    ///
    ///   <item><b>Listener latch state</b> — mirrors what <c>ExclusiveListenerAgent</c> already
    ///   does: ask the runtime for the underlying <see cref="IListeningAgent"/> and translate
    ///   <see cref="ListeningStatus.TooBusy"/> ⇒ Degraded, <see cref="ListeningStatus.GloballyLatched"/>
    ///   ⇒ Unhealthy. Sticky listeners are pinned to specific nodes, so this localizes the
    ///   degradation to the affected node + tenant.</item>
    ///
    ///   <item><b>Per-tenant queue depth</b> — counts rows in the queue table for the
    ///   assigned database. If depth ≥ the parent endpoint's <see cref="BufferingLimits.Maximum"/>
    ///   threshold (when configured), Degraded. Skipped silently when the listener has no
    ///   buffering limits set.</item>
    /// </list>
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (Status != AgentStatus.Running)
        {
            return HealthCheckResult.Unhealthy($"Agent {Uri} is {Status}");
        }

        var degraded = new List<string>(capacity: 4);
        string? unhealthyReason = null;

        // 1) Per-tenant database reachability — SELECT 1 ping
        if (_tenantEndpoint is not null)
        {
            try
            {
                await _tenantEndpoint.PingDatabaseAsync(cancellationToken);
                _consecutiveDbFailures = 0;
            }
            catch (Exception e)
            {
                _consecutiveDbFailures++;
                if (_consecutiveDbFailures >= ConsecutiveDbFailureUnhealthyThreshold)
                {
                    unhealthyReason =
                        $"Per-tenant database '{_databaseName}' unreachable for {_consecutiveDbFailures} consecutive checks: {e.Message}";
                }
                else
                {
                    degraded.Add(
                        $"Per-tenant database '{_databaseName}' poll failed: {e.Message}");
                }
            }

            // 2) Listener latch state — mirror ExclusiveListenerAgent
            var listeningAgent = _runtime.Endpoints.FindListeningAgent(_tenantEndpoint.Uri);
            if (listeningAgent is not null)
            {
                switch (listeningAgent.Status)
                {
                    case ListeningStatus.TooBusy:
                        degraded.Add($"Listener {_queue}/{_databaseName} is too busy");
                        break;
                    case ListeningStatus.GloballyLatched:
                        unhealthyReason ??= $"Listener {_queue}/{_databaseName} is globally latched";
                        break;
                }
            }

            // 3) Per-tenant queue depth — only when the parent endpoint sets a buffering ceiling
            var bufferingLimits = _tenantEndpoint.BufferingLimits;
            if (bufferingLimits is { Maximum: > 0 } && unhealthyReason is null)
            {
                try
                {
                    var depth = await _tenantEndpoint.GetQueueDepthAsync(cancellationToken);
                    if (depth >= bufferingLimits.Maximum)
                    {
                        degraded.Add(
                            $"Queue {_queue}/{_databaseName} depth ({depth}) is at or above the buffering threshold ({bufferingLimits.Maximum})");
                    }
                }
                catch
                {
                    // The DB-reachability ping above already captured the connection issue.
                    // Avoid double-counting toward _consecutiveDbFailures here.
                }
            }
        }

        if (unhealthyReason is not null)
        {
            return HealthCheckResult.Unhealthy(
                degraded.Count == 0
                    ? unhealthyReason
                    : $"{unhealthyReason}; {string.Join("; ", degraded)}");
        }

        return degraded.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(string.Join("; ", degraded));
    }

    /// <summary>
    /// Test-only window into the sticky-listener's consecutive-DB-failure tracker.
    /// </summary>
    internal int ConsecutiveDbFailureCount => _consecutiveDbFailures;
}
