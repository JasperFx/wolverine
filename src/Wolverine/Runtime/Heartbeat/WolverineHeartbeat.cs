namespace Wolverine.Runtime.Heartbeat;

/// <summary>
/// Periodic liveness signal emitted by <see cref="HeartbeatBackgroundService"/> when
/// heartbeats are enabled via <see cref="WolverineOptionsExtensions.EnableHeartbeats"/>.
/// External monitoring tools (e.g. CritterWatch) subscribe to <see cref="WolverineHeartbeat"/>
/// and infer node health from the cadence at which heartbeats arrive.
/// </summary>
/// <remarks>
/// Heartbeats are routed through Wolverine's normal publish pipeline, which means consumers
/// must register an explicit publish rule (for example
/// <c>opts.PublishMessage&lt;WolverineHeartbeat&gt;().ToRabbitExchange("monitoring")</c>) for
/// the heartbeats to leave the local node. With no publish rule and no local subscriber the
/// publish is effectively a no-op.
/// </remarks>
/// <param name="ServiceName">Logical service name from <c>WolverineOptions.ServiceName</c>.</param>
/// <param name="NodeNumber">Locally-assigned node number from <c>Durability.AssignedNodeNumber</c>.</param>
/// <param name="SentAt">UTC timestamp captured when the heartbeat was published.</param>
/// <param name="Uptime">Elapsed time since the heartbeat background service started.</param>
public record WolverineHeartbeat(
    string ServiceName,
    int NodeNumber,
    DateTimeOffset SentAt,
    TimeSpan Uptime);
