using Confluent.Kafka;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

internal class KafkaHealthCheck : WolverineTransportHealthCheck
{
    private readonly KafkaTransport _transport;

    public KafkaHealthCheck(KafkaTransport transport) => _transport = transport;

    public override string TransportName => "Kafka";
    public override string Protocol => "kafka";

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            // Create a short-lived admin client to check broker connectivity
            using var adminClient = _transport.CreateAdminClient();
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(5));

            data["BrokerCount"] = metadata.Brokers.Count;
            data["TopicCount"] = metadata.Topics.Count;

            if (metadata.Brokers.Count == 0)
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

        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data));
    }
}
