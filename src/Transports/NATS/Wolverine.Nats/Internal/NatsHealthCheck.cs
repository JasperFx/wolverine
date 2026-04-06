using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

internal class NatsHealthCheck : WolverineTransportHealthCheck
{
    private readonly NatsTransport _transport;

    public NatsHealthCheck(NatsTransport transport) => _transport = transport;

    public override string TransportName => "NATS";
    public override string Protocol => "nats";

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

        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data));
    }
}
