namespace Wolverine.Transports;

/// <summary>
/// Coarse-grained connection health for a message broker.
/// </summary>
public enum BrokerHealthStatus
{
    /// <summary>
    /// The transport has not yet connected to the broker (e.g., the host has
    /// not started, or the transport has been disabled).
    /// </summary>
    Unknown,

    /// <summary>
    /// The broker connection is open and steady. No recent reconnects.
    /// </summary>
    Healthy,

    /// <summary>
    /// The connection is currently open but has recently flapped (lost and
    /// recovered) or is otherwise in a recovering state.
    /// </summary>
    Degraded,

    /// <summary>
    /// The broker connection is currently down.
    /// </summary>
    Unhealthy
}

/// <summary>
/// Point-in-time, broker-level connection snapshot returned by
/// <see cref="IBrokerHealthProbe"/>.
/// </summary>
/// <param name="TransportUri">The resource URI for this transport (host/vhost
/// for RabbitMQ, namespace for Azure Service Bus, etc.). Never contains
/// credentials.</param>
/// <param name="TransportType">Stable, human-readable transport identifier
/// (e.g. <c>"RabbitMQ"</c>, <c>"AzureServiceBus"</c>) suitable for grouping
/// or display in monitoring UIs.</param>
/// <param name="Status">Current broker connection status.</param>
/// <param name="Description">Optional free-form description (host, vhost,
/// shutdown reason, etc.) for diagnostic display.</param>
/// <param name="CertificateExpiry">If TLS is configured and the certificate
/// is parseable, the certificate's <c>NotAfter</c> timestamp formatted as
/// ISO-8601. Otherwise <c>null</c>.</param>
/// <param name="ReconnectAttempts">Number of times the transport has lost and
/// re-established the broker connection since the host started.</param>
/// <param name="LastSuccessfulAt">Timestamp of the most recent successful
/// connection (initial connect or recovery).</param>
public record BrokerHealthSnapshot(
    Uri TransportUri,
    string TransportType,
    BrokerHealthStatus Status,
    string? Description,
    string? CertificateExpiry,
    int ReconnectAttempts,
    DateTimeOffset LastSuccessfulAt);

/// <summary>
/// Optional contract a Wolverine <c>ITransport</c> can implement to expose a
/// non-destructive, broker-level connection probe to monitoring layers (e.g.
/// CritterWatch).
/// <para>
/// Implementations <b>must not</b> reconnect, bounce the connection, or alter
/// transport state. The probe inspects whatever the transport already holds
/// and returns a snapshot. If the transport has no current connection (e.g.
/// it has not been started), return <see cref="BrokerHealthStatus.Unknown"/>;
/// if the connection is currently down, return
/// <see cref="BrokerHealthStatus.Unhealthy"/> rather than throwing.
/// </para>
/// <para>
/// Probes are discovered by callers via
/// <c>runtime.Options.Transports.OfType&lt;IBrokerHealthProbe&gt;()</c>, so
/// implementing this on the transport itself (as opposed to a separate
/// helper class) is the simplest path.
/// </para>
/// </summary>
public interface IBrokerHealthProbe
{
    /// <summary>
    /// Capture a point-in-time snapshot of the broker connection. Must not
    /// throw if the connection is down — return an <c>Unhealthy</c> snapshot
    /// instead.
    /// </summary>
    Task<BrokerHealthSnapshot> ProbeAsync(CancellationToken ct);
}
