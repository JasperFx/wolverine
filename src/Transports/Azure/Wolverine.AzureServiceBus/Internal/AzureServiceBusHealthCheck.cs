using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

internal class AzureServiceBusHealthCheck : WolverineTransportHealthCheck
{
    private readonly AzureServiceBusTransport _transport;

    public AzureServiceBusHealthCheck(AzureServiceBusTransport transport) => _transport = transport;

    public override string TransportName => "Azure Service Bus";
    public override string Protocol => "asb";

    public override async Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            var managementClient = _transport.ManagementClient;
            data["HasManagementClient"] = true;

            // Lightweight probe: check namespace properties
            var nsProperties = await managementClient.GetNamespacePropertiesAsync(cancellationToken);
            data["Namespace"] = nsProperties?.Value?.Name ?? "unknown";
        }
        catch (Exception ex)
        {
            status = TransportHealthStatus.Unhealthy;
            message = $"Azure Service Bus connectivity check failed: {ex.Message}";
            data["HasManagementClient"] = false;
        }

        return new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data);
    }

    public override async Task<long?> GetBrokerQueueDepthAsync(Uri endpointUri,
        CancellationToken cancellationToken = default)
    {
        if (endpointUri.Scheme != "asb") return null;

        var queueName = endpointUri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(queueName)) return null;

        try
        {
            var managementClient = _transport.ManagementClient;
            var runtimeProps = await managementClient.GetQueueRuntimePropertiesAsync(queueName, cancellationToken);
            return runtimeProps.Value.ActiveMessageCount;
        }
        catch
        {
            // Queue may not exist, or it's a topic/subscription endpoint
            return null;
        }
    }
}
