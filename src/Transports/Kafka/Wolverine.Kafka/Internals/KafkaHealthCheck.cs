using Dekaf.Admin;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

internal class KafkaHealthCheck : WolverineTransportHealthCheck
{
    private readonly KafkaTransport _transport;

    public KafkaHealthCheck(KafkaTransport transport) => _transport = transport;

    public override string TransportName => "Kafka";
    public override string Protocol => "kafka";

    // The transport's own cluster and every tenant's (GH-3303)
    private IEnumerable<KafkaTransport> clusters()
    {
        yield return _transport;
        foreach (var tenant in _transport.Tenants) yield return tenant.Transport;
    }

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var connected = 0;
        var disconnected = 0;
        var fullProducers = 0;

        foreach (var client in clusters().SelectMany(x => x.Clients))
        {
            if (client.FailedToConnect)
            {
                disconnected++;
                continue;
            }

            // Not connected yet, or stopped on purpose
            var status = client.Status;
            if (status == null || status.IsStopped) continue;

            if (KafkaConnectionState.From(status) == TransportConnectionState.Connected)
            {
                connected++;
            }
            else
            {
                disconnected++;
            }

            if (status.Producer is { IsAtCapacity: true }) fullProducers++;
        }

        // Nothing has connected yet, so there is no live status to read
        if (connected + disconnected == 0)
        {
            return probeBrokerAsync(cancellationToken);
        }

        var data = new Dictionary<string, object>
        {
            ["ConnectedClients"] = connected,
            ["DisconnectedClients"] = disconnected,
            ["FullProducerBuffers"] = fullProducers
        };

        var health = TransportHealthStatus.Healthy;
        string? message = null;

        if (connected == 0)
        {
            health = TransportHealthStatus.Unhealthy;
            message = "No Kafka client has a live broker connection";
        }
        else if (disconnected > 0)
        {
            health = TransportHealthStatus.Degraded;
            message = $"{disconnected} of {connected + disconnected} Kafka clients have no live broker connection";
        }
        else if (fullProducers > 0)
        {
            health = TransportHealthStatus.Degraded;
            message = $"{fullProducers} Kafka producer(s) have a full send buffer";
        }

        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, health, message,
            DateTimeOffset.UtcNow, data));
    }

    public override Task<long?> GetBrokerQueueDepthAsync(Uri endpointUri,
        CancellationToken cancellationToken = default)
    {
        // Consumer lag summed over every listener on this endpoint, one per cluster under broker-per-tenant
        long? total = null;
        foreach (var listener in clusters().SelectMany(x => x.Clients).OfType<KafkaListener>())
        {
            if (listener.Address == endpointUri && listener.CurrentLag() is { } lag)
            {
                total = (total ?? 0) + lag;
            }
        }

        return Task.FromResult(total);
    }

    private async Task<TransportHealthResult> probeBrokerAsync(CancellationToken cancellationToken)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            // A short-lived admin client checks broker connectivity
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await using var adminClient = _transport.CreateAdminClient();
            var cluster = await adminClient.DescribeClusterAsync(timeout.Token);
            var topics = await adminClient.ListTopicsAsync(new ListTopicsOptions(), timeout.Token);

            data["BrokerCount"] = cluster.Nodes.Count;
            data["TopicCount"] = topics.Count;

            if (cluster.Nodes.Count == 0)
            {
                status = TransportHealthStatus.Unhealthy;
                message = "No Kafka brokers available";
            }
        }
        catch (Exception ex)
        {
            status = TransportHealthStatus.Unhealthy;
            message = $"Kafka connectivity check failed: {ex.Message}";
        }

        return new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data);
    }
}
