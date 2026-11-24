using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqHealthCheck : WolverineTransportHealthCheck
{
    private readonly RabbitMqTransport _transport;
    private readonly ILogger _logger;

    public RabbitMqHealthCheck(RabbitMqTransport transport, ILogger<RabbitMqHealthCheck>? logger = null)
    {
        _transport = transport;
        _logger = logger ?? NullLogger<RabbitMqHealthCheck>.Instance;
    }

    public override string TransportName => _transport.Name;

    // GH-5946. A named broker's transport has its own scheme, which is what its endpoint URIs carry
    public override string Protocol => _transport.Protocol;

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

    public override async Task<long?> GetBrokerQueueDepthAsync(Uri endpointUri, CancellationToken cancellationToken = default)
    {
        // GH-5946. Every null below says why in the log, because a monitoring tool that gets one cannot tell a
        // broker it could not ask from a transport that does not support the question

        // This transport's own scheme, so an endpoint on a named broker is answered by that broker's health check
        if (!endpointUri.Scheme.Equals(_transport.Protocol, StringComparison.OrdinalIgnoreCase)) return null;

        var queueName = endpointUri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(queueName)) return null;

        // Either connection can count a queue, so a sending connection between a drop and its recovery must not
        // stop the listening one from answering
        var monitor = connected(_transport.TryGetSendingConnection()) ?? connected(_transport.TryGetListeningConnection());
        if (monitor == null)
        {
            _logger.LogDebug(
                "Unable to read the broker queue depth for {Uri} because the Rabbit MQ transport has no connected connection",
                endpointUri);
            return null;
        }

        try
        {
            await using var channel = await monitor.CreateChannelAsync();
            var messageCount = await channel.MessageCountAsync(queueName, cancellationToken);
            return (long)messageCount;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Unable to read the broker queue depth for {Uri} from Rabbit MQ", endpointUri);
            return null;
        }
    }

    private static ConnectionMonitor? connected(ConnectionMonitor? monitor) => monitor is { IsConnected: true } ? monitor : null;
}
