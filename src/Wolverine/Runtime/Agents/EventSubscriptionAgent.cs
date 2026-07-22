using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using JasperFxSubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace Wolverine.Runtime.Agents;

public class EventSubscriptionAgent : IEventSubscriptionAgent
{
    private readonly ShardName _shardName;
    private readonly IProjectionDaemon _daemon;
    private readonly ILogger _logger;
    private JasperFxSubscriptionAgent? _innerAgent;

    // Health check stall tracking
    private long _lastKnownSequence;
    private DateTimeOffset _lastAdvancedAt = DateTimeOffset.UtcNow;
    private int _consecutiveStallCount;

    // Configurable thresholds (loaded from progression table, with defaults)
    private long? _warningThreshold;
    private long? _criticalThreshold;
    private bool _thresholdsLoaded;

    private const long DefaultWarningThreshold = 1000;
    private const long DefaultCriticalThreshold = 5000;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private const int MaxConsecutiveStallsBeforeRestart = 3;

    /// <summary>
    /// Callback fired when the agent is auto-restarted due to stalls.
    /// Parameters: agentUri, reason, timestamp
    /// </summary>
    public Action<string, string, DateTimeOffset>? OnRestarted { get; set; }

    /// <summary>
    /// Set by <see cref="EventStoreAgents.BuildAgentAsync"/> so the owning store can count how many of a
    /// database's agents are running on this node, and let go of that database's tracker subscriptions
    /// when the last one stops.
    /// </summary>
    internal Func<ValueTask>? OnStarted { get; set; }

    internal Func<ValueTask>? OnStopped { get; set; }

    public EventSubscriptionAgent(Uri uri, ShardName shardName, IProjectionDaemon daemon, ILogger logger)
    {
        _shardName = shardName;
        _daemon = daemon;
        _logger = logger;
        Uri = uri;
    }

    public EventSubscriptionAgent(Uri uri, ShardName shardName, IProjectionDaemon daemon)
        : this(uri, shardName, daemon, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await resumeContinuousAsync(cancellationToken);

        // Only count an agent that actually started - a throw above leaves the count untouched
        if (OnStarted != null)
        {
            await OnStarted();
        }
    }

    // Start (or re-adopt) the continuous inner agent through the registered daemon path and refresh the
    // wrapper's view of it. Deliberately does NOT invoke OnStarted: the running-agent bookkeeping in
    // EventStoreAgents counts one logical agent per node for the observer-subscription lifecycle, and a
    // rebuild/rewind is transparent to that count (the wrapper never observed a matching stop), so
    // re-counting here would leak the database's tracker subscriptions. See GH-3520.
    private async Task resumeContinuousAsync(CancellationToken cancellationToken)
    {
        _innerAgent = await _daemon.StartAgentAsync(_shardName, cancellationToken);
        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _daemon.StopAgentAsync(_shardName);
        Status = AgentStatus.Stopped;

        if (OnStopped != null)
        {
            await OnStopped();
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        // Pass the shard's tenant so a per-tenant agent rebuilds ONLY its tenant's partition under
        // single-database per-tenant partitioning. _shardName.TenantId is null for store-global /
        // database-per-tenant shards, where the tenant-less behavior is correct.
        await _daemon.RebuildProjectionAsync(_shardName.Name, _shardName.TenantId, cancellationToken);

        // GH-3520: the daemon-level rebuild stops the continuous agent and never restarts it. Under
        // Wolverine-managed distribution there is no store coordinator to resurrect it, and this wrapper
        // would otherwise keep reporting Running against the now-stopped agent, so NodeAgentController
        // sees nothing to fix and the shard freezes at RegisteredIdle while its high-water climbs.
        // Restore continuous execution ourselves through the registered daemon start path.
        await resumeContinuousAsync(cancellationToken);
    }

    public async Task RewindAsync(long? sequenceFloor, DateTimeOffset? timestamp, CancellationToken cancellationToken)
    {
        // The per-tenant overload delegates to the store-global path when TenantId is null, so this
        // covers both store-global and per-tenant rewinds. This is the agent-level rewind path
        // CritterWatch needs because DaemonForDatabase() throws under Wolverine-managed distribution.
        await _daemon.RewindSubscriptionAsync(_shardName.Name, _shardName.TenantId, cancellationToken,
            sequenceFloor, timestamp);

        // GH-3520: same freeze as RebuildAsync. RewindSubscriptionAsync restarts continuous agents
        // daemon-side, but the wrapper's _innerAgent still points at the stopped pre-rewind agent and
        // Status still reads Running. Re-adopt the live agent through the registered start path so the
        // wrapper reflects reality. This requires JasperFx#536 (rewind registers its restarted agent) so
        // the registered start resolves to that same running agent idempotently rather than spinning up a
        // duplicate on the same progression row - this ships in lockstep with that JasperFx bump.
        await resumeContinuousAsync(cancellationToken);
    }

    public Uri Uri { get; }

    private AgentStatus _status = AgentStatus.Stopped;

    // GH-3519: reflect the LIVE daemon shard status once we have started rather than latching a value.
    // The wedge in the field: a shard that stops or idles underneath this wrapper -- a lost
    // first-assignment start race, a daemon-side stop, or an execution-loop fault -- used to keep
    // reading Running because nothing flipped the latched field back. NodeAgentController only restarts
    // agents it can see are non-Running, so a wrapper that lies "Running" over a dead shard froze at
    // RegisteredIdle forever while its high-water climbed. Delegating to the inner agent (whose Status
    // the daemon keeps current) lets the controller's reevaluation notice the dead shard and restart it.
    // Before an inner agent exists, or after an explicit StopAsync (which sets _status = Stopped), fall
    // back to the wrapper's own tracked value so a not-yet-started / deliberately-stopped agent still
    // reads Stopped.
    public AgentStatus Status
    {
        get => _status == AgentStatus.Stopped ? AgentStatus.Stopped : _innerAgent?.Status ?? _status;
        private set => _status = value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (Status == AgentStatus.Paused)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Projection {Uri} paused due to errors"));
        }

        if (Status != AgentStatus.Running)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Agent {Uri} is {Status}"));
        }

        // Load thresholds on first health check
        if (!_thresholdsLoaded)
        {
            LoadThresholds();
        }

        // GH-3580: a per-tenant agent's Position lives in its OWN tenant's event sequence (per-tenant
        // event partitioning gives every tenant an independent sequence -- the reason the per-tenant
        // fan-out exists, GH-3280), so it must be measured against that tenant's high-water mark, not
        // the database-wide tracker mark. The inner JasperFx agent already carries the tenant-scoped
        // mark: it is seeded from the per-tenant ceiling at start and only ever raised by the tenanted
        // high-water coordinator's per-tenant routing (marten#4717). Comparing against the database-wide
        // mark made every quiet tenant in a busy database read as thousands of "events behind" (a tenant
        // with zero events reported the full database mark) and fed the stall detector the permanently
        // true "currentSequence < highWaterMark", auto-restarting healthy idle agents.
        var highWaterMark = _shardName.TenantId != null
            ? _innerAgent?.HighWaterMark ?? 0
            : _daemon.Tracker.HighWaterMark;
        var currentSequence = _innerAgent?.Position ?? 0;

        var warningThreshold = _warningThreshold ?? DefaultWarningThreshold;
        var criticalThreshold = _criticalThreshold ?? DefaultCriticalThreshold;

        // Check if behind thresholds
        var behindCount = highWaterMark - currentSequence;
        if (behindCount > criticalThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Projection {Uri} is {behindCount} events behind (critical threshold: {criticalThreshold})"));
        }

        if (behindCount > warningThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Projection {Uri} is {behindCount} events behind (warning threshold: {warningThreshold})"));
        }

        // Check for stalled projection
        if (currentSequence != _lastKnownSequence)
        {
            // Sequence has advanced, reset stall tracking
            _lastKnownSequence = currentSequence;
            _lastAdvancedAt = DateTimeOffset.UtcNow;
            _consecutiveStallCount = 0;
        }
        else if (currentSequence < highWaterMark &&
                 DateTimeOffset.UtcNow - _lastAdvancedAt > StallTimeout)
        {
            // Sequence hasn't changed, there are events ahead, and it's been stalled
            _consecutiveStallCount++;

            if (_consecutiveStallCount >= MaxConsecutiveStallsBeforeRestart)
            {
                // Trigger auto-restart
                _ = Task.Run(() => AttemptAutoRestartAsync(cancellationToken), cancellationToken);

                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Projection {Uri} has been stalled for {_consecutiveStallCount} consecutive health checks. Attempting auto-restart."));
            }

            return Task.FromResult(HealthCheckResult.Degraded(
                $"Projection {Uri} stalled at sequence {currentSequence} (high water mark: {highWaterMark})"));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }

    private async Task AttemptAutoRestartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogWarning(
                "Projection {Uri} has been stalled for {StallCount} consecutive health checks. Attempting auto-restart",
                Uri, _consecutiveStallCount);

            await StopAsync(cancellationToken);
            await StartAsync(cancellationToken);

            _consecutiveStallCount = 0;
            _lastAdvancedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Projection {Uri} auto-restart completed successfully", Uri);

            try
            {
                OnRestarted?.Invoke(
                    Uri.ToString(),
                    $"Auto-restarted after {MaxConsecutiveStallsBeforeRestart} consecutive stalled health checks",
                    DateTimeOffset.UtcNow);
            }
            catch (Exception callbackEx)
            {
                _logger.LogDebug(callbackEx, "Error invoking OnRestarted callback for {Uri}", Uri);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-restart projection {Uri}", Uri);
        }
    }

    private void LoadThresholds()
    {
        // Thresholds will be loaded from the progression table when CritterWatch pushes config.
        // For now, use defaults. The threshold columns are read when they exist in the table.
        _warningThreshold = DefaultWarningThreshold;
        _criticalThreshold = DefaultCriticalThreshold;
        _thresholdsLoaded = true;
    }

    /// <summary>
    /// Sets the warning and critical behind thresholds for this agent's health check.
    /// Called when CritterWatch pushes threshold configuration.
    /// </summary>
    public void SetThresholds(long? warningBehindThreshold, long? criticalBehindThreshold)
    {
        _warningThreshold = warningBehindThreshold ?? DefaultWarningThreshold;
        _criticalThreshold = criticalBehindThreshold ?? DefaultCriticalThreshold;
        _thresholdsLoaded = true;
    }
}
