using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Wolverine.Transports;

/// <summary>
/// Wraps a <see cref="WolverineTransportHealthCheck"/> as an ASP.NET Core <see cref="IHealthCheck"/>
/// for integration with the standard /health endpoint.
/// </summary>
internal class WolverineTransportHealthCheckAdapter : IHealthCheck
{
    private readonly WolverineTransportHealthCheck _inner;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Grace period after startup during which the health check reports Healthy
    /// even if the transport hasn't connected yet. Prevents false Unhealthy reports
    /// during application initialization.
    /// </summary>
    public static TimeSpan StartupGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public WolverineTransportHealthCheckAdapter(WolverineTransportHealthCheck inner)
    {
        _inner = inner;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.CheckHealthAsync(cancellationToken);

        // During startup grace period, report Healthy even if Degraded/Unhealthy
        if (result.Status != TransportHealthStatus.Healthy &&
            DateTimeOffset.UtcNow - _startedAt < StartupGracePeriod)
        {
            return HealthCheckResult.Healthy(
                $"Startup grace period ({StartupGracePeriod.TotalSeconds}s) — {result.Message ?? "initializing"}",
                result.Data);
        }

        return result.Status switch
        {
            TransportHealthStatus.Healthy => HealthCheckResult.Healthy(result.Message, result.Data),
            TransportHealthStatus.Degraded => HealthCheckResult.Degraded(result.Message, data: result.Data),
            TransportHealthStatus.Unhealthy => HealthCheckResult.Unhealthy(result.Message, data: result.Data),
            _ => HealthCheckResult.Unhealthy($"Unknown status: {result.Status}")
        };
    }
}
