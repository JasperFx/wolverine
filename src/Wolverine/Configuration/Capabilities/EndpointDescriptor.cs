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
        SerializerType = endpoint.DefaultSerializer?.GetType().Name;
        InteropMode = ResolveInteropMode(endpoint);
        IsSystemEndpoint = endpoint.Uri?.ToString().Contains("wolverine.response", StringComparison.OrdinalIgnoreCase) == true
                        || endpoint.Uri?.Scheme.Equals("local", StringComparison.OrdinalIgnoreCase) == true;
    }

    public Uri Uri { get; set; } = null!;

    /// <summary>
    /// Human-readable description of the transport type (e.g., "RabbitMQ Queue", "Local Queue", "Kafka Topic")
    /// </summary>
    public string? TransportType { get; init; }

    /// <summary>
    /// The serializer type name (e.g., "SystemTextJsonSerializer", "MessagePackSerializer")
    /// </summary>
    public string? SerializerType { get; init; }

    /// <summary>
    /// Interop mode if using a pre-canned interop format.
    /// Values: "CloudEvents", "NServiceBus", "MassTransit", "RawJson", or null for default Wolverine format.
    /// </summary>
    public string? InteropMode { get; init; }

    /// <summary>
    /// Whether this is a Wolverine system endpoint (reply queue, control queue, etc.)
    /// </summary>
    public bool IsSystemEndpoint { get; init; }

    internal static string? ResolveInteropMode(Endpoint endpoint)
    {
        return ResolveInteropMode(endpoint.DefaultSerializer?.GetType().Name);
    }

    public static string? ResolveInteropMode(string? serializerTypeName)
    {
        if (string.IsNullOrEmpty(serializerTypeName)) return null;
        if (serializerTypeName.Contains("CloudEvents", StringComparison.OrdinalIgnoreCase)) return "CloudEvents";
        if (serializerTypeName.Contains("NServiceBus", StringComparison.OrdinalIgnoreCase)) return "NServiceBus";
        if (serializerTypeName.Contains("MassTransit", StringComparison.OrdinalIgnoreCase)) return "MassTransit";
        if (serializerTypeName.Contains("RawJson", StringComparison.OrdinalIgnoreCase)) return "RawJson";
        return null;
    }

    internal static string ResolveTransportType(Endpoint endpoint) => ResolveTransportType(endpoint.GetType().Name);

    public static string ResolveTransportType(string typeName)
    {

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