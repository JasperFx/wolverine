namespace Wolverine;

/// <summary>
/// Configures the periodic <see cref="Wolverine.Runtime.Heartbeat.WolverineHeartbeat"/>
/// emission performed by <see cref="Wolverine.Runtime.Heartbeat.HeartbeatBackgroundService"/>.
/// Heartbeats are intended to give external monitoring tools (e.g. CritterWatch) a simple
/// liveness signal: each running node publishes a tiny message at a regular cadence, and
/// missing heartbeats indicate that a node has gone dark.
/// </summary>
/// <remarks>
/// The hosted service that emits heartbeats is registered through
/// <see cref="WolverineOptionsExtensions.EnableHeartbeats"/>. Setting <see cref="Enabled"/> to
/// <c>false</c> after registration will cause the background service to exit immediately on
/// startup so heartbeats can be selectively disabled per environment without removing the
/// registration.
/// </remarks>
public class HeartbeatPolicy
{
    /// <summary>
    /// Whether heartbeat emission is enabled. Defaults to <c>true</c>. The hosted service
    /// only runs when <see cref="WolverineOptionsExtensions.EnableHeartbeats"/> has been
    /// invoked; this flag controls whether that registered service actually publishes.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval between successive <see cref="Wolverine.Runtime.Heartbeat.WolverineHeartbeat"/>
    /// publishes. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}
