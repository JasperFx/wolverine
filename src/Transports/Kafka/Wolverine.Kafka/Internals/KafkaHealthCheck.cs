using Dekaf.Admin;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

internal class KafkaHealthCheck : WolverineTransportHealthCheck
{
    private readonly KafkaTransport _transport;

    public KafkaHealthCheck(KafkaTransport transport) => _transport = transport;

    public override string TransportName => "Kafka";
    public override string Protocol => "kafka";

    public override async Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            // Create a short-lived admin client to check broker connectivity
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
