namespace Wolverine.Transports;

/// <summary>
/// Point-in-time health status for a message transport.
/// </summary>
public enum TransportHealthStatus
{
    /// <summary>
    /// All endpoints accepting, connections alive, no circuit breakers tripped.
    /// </summary>
    Healthy,

    /// <summary>
    /// Some endpoints in TooBusy state, some senders latched but recovering,
    /// or connection temporarily lost with auto-recovery in progress.
    /// </summary>
    Degraded,

    /// <summary>
    /// Transport connection fully down, all senders circuit-broken,
    /// or listener permanently latched.
    /// </summary>
    Unhealthy
}

/// <summary>
/// Result of a transport health check.
/// </summary>
public record TransportHealthResult(
    string TransportName,
    string Protocol,
    TransportHealthStatus Status,
    string? Message,
    DateTimeOffset CheckedAt,
    Dictionary<string, object>? Data = null);

/// <summary>
/// Base class for transport-specific health checks. Each transport that has
/// connection state or broker health to monitor should implement this.
/// </summary>
public abstract class WolverineTransportHealthCheck
{
    public abstract string TransportName { get; }
    public abstract string Protocol { get; }

    /// <summary>
    /// Check the health of this transport. Implementations should be lightweight
    /// and read existing state rather than making expensive active probes where possible.
    /// </summary>
    public abstract Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}
