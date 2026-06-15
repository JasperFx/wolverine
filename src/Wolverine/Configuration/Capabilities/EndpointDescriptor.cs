using System.Diagnostics.CodeAnalysis;
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

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "OptionsDescription(subject) reads subject.GetType().GetProperties() to build a diagnostic description. EndpointDescriptor is a diagnostic surface (Capabilities reporting); properties of Endpoint subclasses that are trimmed are silently omitted, which is acceptable here.")]
    public EndpointDescriptor(Endpoint endpoint) : base(endpoint)
    {
        Uri = endpoint.Uri;
        TransportType = ResolveTransportType(endpoint);
        SerializerType = endpoint.DefaultSerializer?.GetType().Name;
        DefaultSerializerDescription = ResolveSerializerDescription(SerializerType);
        InteropMode = ResolveInteropMode(endpoint);
        IsSystemEndpoint = endpoint.Uri?.ToString().Contains("wolverine.response", StringComparison.OrdinalIgnoreCase) == true
                        || endpoint.Uri?.Scheme.Equals("local", StringComparison.OrdinalIgnoreCase) == true;
        EndpointRole = endpoint.Role;
        BrokerRole = endpoint.BrokerRole;
        Mode = endpoint.Mode;
        IsListener = endpoint.IsListener;
        DeadLetterStorage = endpoint.DeadLetterStorage;

        // Mode and IsListener are now first-class typed fields (GH-3009); DeadLetterStorage is
        // likewise a typed field (GH-3104). Drop the generic OptionsDescription rows the base ctor
        // reflected off the Endpoint so we don't ship them twice — CritterWatch reads the typed
        // fields at the service-overview level, and Properties is becoming lazy-fetchable downstream.
        Properties.RemoveAll(x =>
            x.Name is nameof(Endpoint.Mode) or nameof(Endpoint.IsListener) or nameof(Endpoint.DeadLetterStorage));
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
    /// Human-readable name of the default serializer wired up for this endpoint
    /// (e.g. <c>"System.Text.Json"</c>, <c>"MessagePack"</c>, <c>"CloudEvents"</c>).
    /// Maps the framework's well-known <see cref="SerializerType"/> values to a
    /// friendlier rendering for monitoring tools so operators don't have to recognize
    /// the raw type name. Falls through to the raw type name when no friendly mapping
    /// exists. <c>null</c> when no serializer is configured. See #2641.
    /// </summary>
    public string? DefaultSerializerDescription { get; init; }

    /// <summary>
    /// Interop mode for this endpoint. Values:
    /// <list type="bullet">
    ///   <item><c>"CloudEvents"</c> / <c>"NServiceBus"</c> / <c>"MassTransit"</c> / <c>"RawJson"</c> — well-known interop formats detected by serializer name.</item>
    ///   <item><c>"Custom"</c> — the endpoint has a non-default <see cref="IEnvelopeMapper"/> wired in (mapper signal takes precedence over serializer name; #2641).</item>
    ///   <item><c>null</c> — default Wolverine format.</item>
    /// </list>
    /// </summary>
    public string? InteropMode { get; init; }

    /// <summary>
    /// Whether this is a Wolverine system endpoint (reply queue, control queue, etc.)
    /// </summary>
    public bool IsSystemEndpoint { get; init; }

    /// <summary>
    /// Whether the endpoint is owned by Wolverine itself (<c>System</c>) or by the
    /// application (<c>Application</c>). Lifted from <see cref="Endpoint.Role"/> so
    /// CritterWatch and other UIs can filter system-owned endpoints (e.g., reply
    /// queues, control queues) without having to crack the underlying URI. See GH-2601.
    /// </summary>
    public EndpointRole EndpointRole { get; init; }

    /// <summary>
    /// Short, human-readable name of the underlying broker object kind this endpoint
    /// represents — <c>"queue"</c>, <c>"exchange"</c>, <c>"topic"</c>,
    /// <c>"subscription"</c>, <c>"stream"</c>, etc. Lifted from
    /// <see cref="Endpoint.BrokerRole"/>; see that property for the full per-transport
    /// mapping. See GH-2601.
    /// </summary>
    public string? BrokerRole { get; init; }

    /// <summary>
    /// How this endpoint processes messages — <c>Durable</c> (persistence-backed inbox/outbox),
    /// <c>BufferedInMemory</c>, or <c>Inline</c>. Lifted from <see cref="Endpoint.Mode"/> into a
    /// first-class typed field (GH-3009) so CritterWatch and other UIs can read it at the
    /// service-overview level without walking <see cref="OptionsDescription.Properties"/>.
    /// </summary>
    public EndpointMode Mode { get; init; }

    /// <summary>
    /// Whether this endpoint is actively listening for (receiving) messages. Lifted from
    /// <see cref="Endpoint.IsListener"/> into a first-class typed field (GH-3009) so monitoring
    /// tools can read it at the service-overview level without walking
    /// <see cref="OptionsDescription.Properties"/>.
    /// </summary>
    public bool IsListener { get; init; }

    /// <summary>
    /// Where this endpoint's dead letters effectively go — <see cref="DeadLetterStorageMode.Durable"/>
    /// (Wolverine's durable, queryable store), <see cref="DeadLetterStorageMode.Native"/> (a native
    /// broker dead letter queue, un-bridged), or <see cref="DeadLetterStorageMode.NativeWithRecovery"/>
    /// (native, but bridged back into durable storage). A transport-agnostic contract (GH-3104) so
    /// monitoring tools like CritterWatch can detect endpoints that dead-letter natively without
    /// recovery — and recommend enabling it — without transport-specific knowledge. Lifted from
    /// <see cref="Endpoint.DeadLetterStorage"/>.
    /// </summary>
    public DeadLetterStorageMode DeadLetterStorage { get; init; }

    internal static string? ResolveInteropMode(Endpoint endpoint)
    {
        // Mapper signal wins over serializer signal (last-usage-wins). If the user
        // wired their own envelope mapper for this endpoint, that's the louder
        // operator-relevant signal regardless of which serializer happens to be
        // attached. See #2641.
        if (endpoint.HasCustomEnvelopeMapper) return "Custom";

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

    /// <summary>
    /// Map a raw serializer type name to a friendly display string for
    /// <see cref="DefaultSerializerDescription"/>. Well-known names get
    /// human-readable equivalents (<c>"SystemTextJsonSerializer"</c> →
    /// <c>"System.Text.Json"</c>, etc.); anything else falls through to the
    /// raw type name unchanged.
    /// </summary>
    public static string? ResolveSerializerDescription(string? serializerTypeName)
    {
        if (string.IsNullOrEmpty(serializerTypeName)) return null;

        // Order matters: more specific matches first (e.g. CloudEventsJsonSerializer
        // hits the CloudEvents branch before falling into the System.Text.Json branch).
        if (serializerTypeName.Contains("CloudEvents", StringComparison.OrdinalIgnoreCase)) return "CloudEvents";
        if (serializerTypeName.Contains("NServiceBus", StringComparison.OrdinalIgnoreCase)) return "NServiceBus";
        if (serializerTypeName.Contains("MassTransit", StringComparison.OrdinalIgnoreCase)) return "MassTransit";
        if (serializerTypeName.Contains("RawJson", StringComparison.OrdinalIgnoreCase)) return "Raw JSON";
        if (serializerTypeName.Contains("SystemTextJson", StringComparison.OrdinalIgnoreCase)) return "System.Text.Json";
        if (serializerTypeName.Contains("Newtonsoft", StringComparison.OrdinalIgnoreCase)) return "Newtonsoft.Json";
        if (serializerTypeName.Contains("MessagePack", StringComparison.OrdinalIgnoreCase)) return "MessagePack";
        if (serializerTypeName.Contains("MemoryPack", StringComparison.OrdinalIgnoreCase)) return "MemoryPack";
        if (serializerTypeName.Contains("Protobuf", StringComparison.OrdinalIgnoreCase)) return "Protobuf";
        if (serializerTypeName.Contains("Avro", StringComparison.OrdinalIgnoreCase)) return "Avro";

        return serializerTypeName;
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