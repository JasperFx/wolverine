using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqHealthCheck : WolverineTransportHealthCheck
{
    private readonly RabbitMqTransport _transport;

    public RabbitMqHealthCheck(RabbitMqTransport transport)
    {
        _transport = transport;
    }

    public override string TransportName => _transport.Name;
    public override string Protocol => RabbitMqTransport.ProtocolName;

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var status = TransportHealthStatus.Healthy;
        string? message = null;

        var listeningConnection = _transport.TryGetListeningConnection();
        var sendingConnection = _transport.TryGetSendingConnection();

        // Check listening connection
        if (listeningConnection != null)
        {
            data["ListeningConnectionAlive"] = listeningConnection.IsConnected;
            data["ListeningConnectionBlocked"] = listeningConnection.IsBlocked;

            if (!listeningConnection.IsConnected)
            {
                status = TransportHealthStatus.Unhealthy;
                message = "RabbitMQ listening connection is down";
            }
            else if (listeningConnection.IsBlocked)
            {
                status = TransportHealthStatus.Degraded;
                message = "RabbitMQ listening connection is blocked (broker resource alarm)";
            }
        }

        // Check sending connection
        if (sendingConnection != null)
        {
            data["SendingConnectionAlive"] = sendingConnection.IsConnected;
            data["SendingConnectionBlocked"] = sendingConnection.IsBlocked;

            if (!sendingConnection.IsConnected)
            {
                status = TransportHealthStatus.Unhealthy;
                message = "RabbitMQ sending connection is down";
            }
            else if (sendingConnection.IsBlocked && status != TransportHealthStatus.Unhealthy)
            {
                status = TransportHealthStatus.Degraded;
                message = "RabbitMQ sending connection is blocked (broker resource alarm)";
            }
        }

        // If neither connection exists, the transport hasn't been initialized yet
        if (listeningConnection == null && sendingConnection == null)
        {
            status = TransportHealthStatus.Degraded;
            message = "RabbitMQ transport not yet initialized";
        }

        var result = new TransportHealthResult(
            TransportName: TransportName,
            Protocol: Protocol,
            Status: status,
            Message: message,
            CheckedAt: DateTimeOffset.UtcNow,
            Data: data);

        return Task.FromResult(result);
    }
}
