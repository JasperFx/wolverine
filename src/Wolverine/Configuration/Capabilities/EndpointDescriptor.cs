using System.Text.RegularExpressions;
using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

public class EndpointDescriptor : OptionsDescription
{
    private static readonly Dictionary<string, string> TransportTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "RabbitMqQueue", "RabbitMQ Queue" },
        { "RabbitMqExchange", "RabbitMQ Exchange" },
        { "RabbitMqTopicEndpoint", "RabbitMQ Topic" },
        { "RabbitMqRouting", "RabbitMQ Routing" },
        { "AzureServiceBusQueue", "Azure Service Bus Queue" },
        { "AzureServiceBusTopic", "Azure Service Bus Topic" },
        { "AzureServiceBusSubscription", "Azure Service Bus Subscription" },
        { "LocalQueue", "Local Queue" },
        { "NatsEndpoint", "NATS" },
        { "RedisStreamEndpoint", "Redis Stream" },
        { "PubsubEndpoint", "GCP Pub/Sub" },
        { "PulsarEndpoint", "Pulsar" },
        { "SignalRClientEndpoint", "SignalR" },
    };

    public EndpointDescriptor()
    {
    }

    public EndpointDescriptor(Endpoint endpoint) : base(endpoint)
    {
        Uri = endpoint.Uri;
        TransportType = ResolveTransportType(endpoint);
    }

    public Uri Uri { get; set; } = null!;

    /// <summary>
    /// Human-readable description of the transport type (e.g., "RabbitMQ Queue", "Local Queue", "Kafka Topic")
    /// </summary>
    public string? TransportType { get; init; }

    internal static string ResolveTransportType(Endpoint endpoint)
    {
        var typeName = endpoint.GetType().Name;

        if (TransportTypeMap.TryGetValue(typeName, out var mapped))
        {
            return mapped;
        }

        // Fallback: split PascalCase into words (e.g., "KafkaTopicEndpoint" → "Kafka Topic Endpoint")
        return Regex.Replace(typeName, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
    }

    protected bool Equals(EndpointDescriptor other)
    {
        return Uri.Equals(other.Uri);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((EndpointDescriptor)obj);
    }

    public override int GetHashCode()
    {
        return Uri.GetHashCode();
    }
}