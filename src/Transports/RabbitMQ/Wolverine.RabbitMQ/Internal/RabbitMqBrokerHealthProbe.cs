using System.Security.Cryptography.X509Certificates;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

/// <summary>
/// Implements <see cref="IBrokerHealthProbe"/> for the RabbitMQ transport. The
/// probe is non-destructive: it inspects state already held by the transport's
/// listening / sending connections (open flag, last shutdown reason, recent
/// reconnect activity) without opening new channels or bouncing the connection.
/// </summary>
public partial class RabbitMqTransport : IBrokerHealthProbe
{
    /// <summary>
    /// How long after a successful (re)connection the broker is reported as
    /// <see cref="BrokerHealthStatus.Degraded"/> rather than <c>Healthy</c>.
    /// Anything more recent than this is treated as a "just flapped" signal.
    /// </summary>
    private static readonly TimeSpan RecentReconnectWindow = TimeSpan.FromMinutes(2);

    private int _reconnectAttempts;
    private DateTimeOffset _lastSuccessfulAt;
    private string? _lastShutdownDescription;

    /// <summary>
    /// Number of times the broker connection has been re-established by the
    /// RabbitMQ client's auto-recovery loop since the host started. The initial
    /// connect at startup does <b>not</b> count.
    /// </summary>
    public int ReconnectAttempts => Volatile.Read(ref _reconnectAttempts);

    /// <summary>
    /// Timestamp of the most recent successful (re)connection.
    /// <see cref="DateTimeOffset.MinValue"/> until the first connect succeeds.
    /// </summary>
    public DateTimeOffset LastSuccessfulConnectionAt => _lastSuccessfulAt;

    internal void RecordInitialConnection()
    {
        _lastSuccessfulAt = DateTimeOffset.UtcNow;
    }

    internal void RecordReconnection()
    {
        Interlocked.Increment(ref _reconnectAttempts);
        _lastSuccessfulAt = DateTimeOffset.UtcNow;
    }

    internal void RecordShutdown(ShutdownEventArgs e)
    {
        // Record the close reason for inclusion in the next probe snapshot.
        _lastShutdownDescription = e.ToString();
    }

    public Task<BrokerHealthSnapshot> ProbeAsync(CancellationToken ct)
    {
        var listening = TryGetListeningConnection();
        var sending = TryGetSendingConnection();

        // Neither connection has been built yet -- transport not started or disabled.
        if (listening == null && sending == null)
        {
            return Task.FromResult(new BrokerHealthSnapshot(
                TransportUri: ResourceUri,
                TransportType: "RabbitMQ",
                Status: BrokerHealthStatus.Unknown,
                Description: "RabbitMQ transport has not been started",
                CertificateExpiry: TryReadCertificateExpiry(),
                ReconnectAttempts: ReconnectAttempts,
                LastSuccessfulAt: _lastSuccessfulAt));
        }

        var listenerOpen = listening?.Connection?.IsOpen ?? true;
        var senderOpen = sending?.Connection?.IsOpen ?? true;

        BrokerHealthStatus status;
        string? description;

        if (!listenerOpen || !senderOpen)
        {
            status = BrokerHealthStatus.Unhealthy;

            var listenerReason = listening?.Connection?.CloseReason?.ToString();
            var senderReason = sending?.Connection?.CloseReason?.ToString();
            description = listenerReason ?? senderReason ?? _lastShutdownDescription
                ?? "RabbitMQ connection is not open";
        }
        else if (_reconnectAttempts > 0
                 && (DateTimeOffset.UtcNow - _lastSuccessfulAt) <= RecentReconnectWindow)
        {
            // We've reconnected at least once and it was recent -- mark as Degraded
            // so that monitoring layers can flag flapping connections.
            status = BrokerHealthStatus.Degraded;
            description = $"RabbitMQ connection recovered at {_lastSuccessfulAt:O} ({_reconnectAttempts} reconnect{(_reconnectAttempts == 1 ? "" : "s")}).";
        }
        else
        {
            status = BrokerHealthStatus.Healthy;
            description = describeOpenConnection();
        }

        return Task.FromResult(new BrokerHealthSnapshot(
            TransportUri: ResourceUri,
            TransportType: "RabbitMQ",
            Status: status,
            Description: description,
            CertificateExpiry: TryReadCertificateExpiry(),
            ReconnectAttempts: ReconnectAttempts,
            LastSuccessfulAt: _lastSuccessfulAt));
    }

    private string? describeOpenConnection()
    {
        if (ConnectionFactory == null) return null;
        var vhost = ConnectionFactory.VirtualHost;
        return string.IsNullOrEmpty(vhost) || vhost == "/"
            ? $"Connected to {ConnectionFactory.HostName}"
            : $"Connected to {ConnectionFactory.HostName} (vhost {vhost})";
    }

    private string? TryReadCertificateExpiry()
    {
        var ssl = ConnectionFactory?.Ssl;
        if (ssl == null || !ssl.Enabled) return null;
        if (string.IsNullOrEmpty(ssl.CertPath)) return null;

        try
        {
            using var cert = LoadCertificate(ssl.CertPath, ssl.CertPassphrase);
            return cert.NotAfter.ToUniversalTime().ToString("O");
        }
        catch
        {
            // We deliberately swallow here -- a bad cert path shouldn't make the
            // probe throw; it just means the expiry is unavailable.
            return null;
        }
    }

    private static X509Certificate2 LoadCertificate(string certPath, string? passphrase)
    {
#if NET9_0_OR_GREATER
        return string.IsNullOrEmpty(passphrase)
            ? X509CertificateLoader.LoadCertificateFromFile(certPath)
            : X509CertificateLoader.LoadPkcs12FromFile(certPath, passphrase);
#else
#pragma warning disable SYSLIB0057
        return string.IsNullOrEmpty(passphrase)
            ? new X509Certificate2(certPath)
            : new X509Certificate2(certPath, passphrase);
#pragma warning restore SYSLIB0057
#endif
    }
}
