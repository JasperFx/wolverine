using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;
using ISubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace Wolverine.Marten.Distribution;

public class EventSubscriptionAgent : IEventSubscriptionAgent
{
    private readonly ShardName _shardName;
    private readonly IProjectionDaemon _daemon;
    private readonly ILogger _logger;
    private ISubscriptionAgent? _innerAgent;

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
        _innerAgent = await _daemon.StartAgentAsync(_shardName, cancellationToken);
        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _daemon.StopAgentAsync(_shardName);
        Status = AgentStatus.Stopped;
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await _daemon.RebuildProjectionAsync(_shardName.Name, cancellationToken);
    }

    public Uri Uri { get; }

    // Be nice for this to get the Paused too
    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

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

        var tracker = _daemon.Tracker;
        var highWaterMark = tracker.HighWaterMark;
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
