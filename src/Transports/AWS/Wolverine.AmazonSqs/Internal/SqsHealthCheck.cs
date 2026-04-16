using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsHealthCheck : WolverineTransportHealthCheck
{
    private readonly AmazonSqsTransport _transport;

    public SqsHealthCheck(AmazonSqsTransport transport) => _transport = transport;

    public override string TransportName => "Amazon SQS";
    public override string Protocol => "sqs";

    public override async Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        var client = _transport.Client;
        data["HasClient"] = client != null;

        if (client == null)
        {
            return new TransportHealthResult(TransportName, Protocol, TransportHealthStatus.Degraded,
                "SQS client not yet initialized", DateTimeOffset.UtcNow, data);
        }

        try
        {
            // Lightweight probe: list queues with a limit
            var response = await client.ListQueuesAsync("wolverine", cancellationToken);
            data["QueueCount"] = response.QueueUrls?.Count ?? 0;
        }
        catch (Exception ex)
        {
            status = TransportHealthStatus.Unhealthy;
            message = $"SQS connectivity check failed: {ex.Message}";
        }

        return new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data);
    }

    public override async Task<long?> GetBrokerQueueDepthAsync(Uri endpointUri,
        CancellationToken cancellationToken = default)
    {
        if (endpointUri.Scheme != "sqs") return null;

        var client = _transport.Client;
        if (client == null) return null;

        var queueName = endpointUri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(queueName)) return null;

        try
        {
            // Find the queue URL
            var endpoint = _transport.Queues[queueName];
            var queueUrl = endpoint?.QueueUrl;
            if (queueUrl == null) return null;

            var response = await client.GetQueueAttributesAsync(
                queueUrl,
                new List<string> { "ApproximateNumberOfMessages" },
                cancellationToken);

            if (response.Attributes.TryGetValue("ApproximateNumberOfMessages", out var countStr) &&
                long.TryParse(countStr, out var count))
            {
                return count;
            }
        }
        catch
        {
            // Queue may not exist or access denied
        }

        return null;
    }
}
