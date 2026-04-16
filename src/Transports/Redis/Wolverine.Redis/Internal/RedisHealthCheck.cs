using Wolverine.Transports;

namespace Wolverine.Redis.Internal;

internal class RedisHealthCheck : WolverineTransportHealthCheck
{
    private readonly RedisTransport _transport;

    public RedisHealthCheck(RedisTransport transport) => _transport = transport;

    public override string TransportName => "Redis";
    public override string Protocol => "redis";

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            var connection = _transport.GetConnection();
            data["IsConnected"] = connection.IsConnected;

            if (!connection.IsConnected)
            {
                status = TransportHealthStatus.Unhealthy;
                message = "Redis connection is not connected";
            }
        }
        catch
        {
            status = TransportHealthStatus.Degraded;
            message = "Redis transport not yet initialized";
        }

        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data));
    }

    public override async Task<long?> GetBrokerQueueDepthAsync(Uri endpointUri,
        CancellationToken cancellationToken = default)
    {
        if (endpointUri.Scheme != "redis") return null;

        var streamKey = endpointUri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(streamKey)) return null;

        try
        {
            var connection = _transport.GetConnection();
            if (!connection.IsConnected) return null;

            var db = connection.GetDatabase();
            return await db.StreamLengthAsync(streamKey);
        }
        catch
        {
            return null;
        }
    }
}
