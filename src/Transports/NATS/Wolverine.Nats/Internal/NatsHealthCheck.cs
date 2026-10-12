using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

internal class NatsHealthCheck : WolverineTransportHealthCheck
{
    private readonly NatsTransport _transport;

    public NatsHealthCheck(NatsTransport transport) => _transport = transport;

    public override string TransportName => "NATS";
    public override string Protocol => _transport.Protocol;

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            var connection = _transport.Connection;
            var serverInfo = connection.ServerInfo;
            data["HasConnection"] = true;
            data["ServerVersion"] = serverInfo?.Version ?? "unknown";

            if (serverInfo == null)
            {
                status = TransportHealthStatus.Degraded;
                message = "NATS connected but no server info available";
            }
        }
        catch
        {
            status = TransportHealthStatus.Degraded;
            message = "NATS transport not yet initialized";
            data["HasConnection"] = false;
        }

        // A listener started after the host -- an exclusive listener, a shard -- that failed to start is down
        // without anything else saying so
        var failedListeners = _transport.FailedListenerStarts();
        if (failedListeners.Length > 0)
        {
            status = TransportHealthStatus.Unhealthy;
            message = "NATS listeners failed to start: " +
                      string.Join("; ", failedListeners.Select(x => $"{x.Key} ({x.Value})"));
            data["FailedListeners"] = failedListeners.Select(x => x.Key.ToString()).ToArray();
        }

        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data));
    }
}
