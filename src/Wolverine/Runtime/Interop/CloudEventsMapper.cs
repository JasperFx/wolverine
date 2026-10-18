using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using MassTransit;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Runtime.Interop;

public class UnknownMessageTypeNameException : Exception
{
    public UnknownMessageTypeNameException(string? message) : base(message)
    {
    }
}

internal class CloudEventsEnvelope
{
    public CloudEventsEnvelope()
    {
    }

    public CloudEventsEnvelope(Envelope envelope)
    {
        if (envelope.Message is null) throw new ArgumentNullException(nameof(envelope), "Message is null");
        
        TenantId = envelope.TenantId!;
        Data = envelope.Message;

        // Doesn't always apply in Wolverine, so ¯\_(ツ)_/¯
        Topic = (envelope.TopicName ?? envelope.GroupId)!;

        Id = envelope.Id;
        TraceId = envelope.CorrelationId!;
        Source = envelope.Source!;

        // This is the Wolverine string that aliases the message type
        Type = envelope.MessageType!;

        Time = envelope.SentAt.ToString("O");

        TraceParent = envelope.ParentId!;

        // GH-5080: custom headers ride along as CloudEvents extension attributes so they survive
        // transports where the CloudEvents document is the whole message (SQS, SNS, HTTP, SignalR)
        if (envelope.HasHeaders)
        {
            foreach (var pair in envelope.Headers)
            {
                if (pair.Value != null && CloudEventsMapper.IsExtensionAttribute(pair.Key))
                {
                    Extensions ??= new Dictionary<string, object>();
                    Extensions[pair.Key] = pair.Value;
                }
            }
        }
    }

    /// <summary>
    /// CloudEvents extension attributes, written at the top level of the document. Outgoing, these are
    /// the envelope's lower case custom headers
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? Extensions { get; set; }

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = null!;

    [JsonPropertyName("tenantid")]
    public string TenantId { get; set; } = null!;

    [JsonPropertyName("traceid")]
    public string TraceId { get; set; } = null!;

    [JsonPropertyName("tracestate")]
    public string TraceState { get; set; } = null!;

    [JsonPropertyName("data")]
    public object Data { get; set; } = null!;

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("specversion")]
    public string SpecVersion { get; set; } = "1.0";

    [JsonPropertyName("datacontenttype")]
    public string DataContentType { get; set; } = "application/json; charset=utf-8";

    [JsonPropertyName("source")]
    public string Source { get; set; } = null!;

    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("time")]
    public string Time { get; set; } = null!;

    [JsonPropertyName("traceparent")]
    public string TraceParent { get; set; } = null!;
}

public class CloudEventsMapper : IUnwrapsMetadataMessageSerializer
{
    private readonly HandlerGraph _handlers;
    private readonly JsonSerializerOptions _options;

    public CloudEventsMapper(HandlerGraph handlers, JsonSerializerOptions options)
    {
        _handlers = handlers;
        _options = options;
    }

    public override string ToString() => "Cloud Events";

    // Names that are already CloudEvents context attributes, or that Wolverine itself writes into the
    // CloudEvents document, so they can never be used for a custom header
    private static readonly HashSet<string> _reservedAttributeNames = new(StringComparer.Ordinal)
    {
        "id", "source", "specversion", "type", "datacontenttype", "dataschema", "subject", "time",
        "data", "data_base64", "topic", "tenantid", "traceid", "tracestate", "traceparent"
    };

    /// <summary>
    /// Is this header name carried as a CloudEvents extension attribute? Only lower case names are, and
    /// never a CloudEvents context attribute or one of Wolverine's own envelope header keys
    /// </summary>
    internal static bool IsExtensionAttribute(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var c in name)
        {
            if (char.IsUpper(c)) return false;
        }

        return !_reservedAttributeNames.Contains(name)
               && !EnvelopeSerializer.ReservedHeaderKeys.Contains(name)
               && name != EnvelopeConstants.SentAtKey
               && name != TransportConstants.ProtocolVersion;
    }

    // CloudEvents interop layer wraps the user message inside CloudEventsEnvelope
    // and serializes the wrapper with the default reflection-based STJ overloads
    // (no JsonTypeInfo / JsonSerializerContext). The wrapper itself is statically
    // known here, but `Data` is `object` carrying an arbitrary user message — so
    // even if we taught this call site to use a JsonTypeInfo for the wrapper, the
    // inner-payload reflection survives. Treat this as IMessageSerializer-style
    // default JSON: suppress at the leaf with an AOT-guide-pointing justification
    // rather than cascading `[Requires*]` through the IMessageSerializer /
    // IUnwrapsMetadataMessageSerializer surface and every implementation.
    //
    // AOT-clean apps that need CloudEvents interop should supply their own
    // IUnwrapsMetadataMessageSerializer that wraps JsonSerializer with a
    // JsonSerializerContext covering both CloudEventsEnvelope and the message
    // payload types. See the Wolverine AOT publishing guide.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CloudEvents interop default serializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CloudEvents interop default serializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    public string WriteToString(Envelope envelope)
    {
        return JsonSerializer.Serialize(new CloudEventsEnvelope(envelope), _options);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CloudEvents interop default serializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CloudEvents interop default serializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    public byte[] WriteToBytes(Envelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new CloudEventsEnvelope(envelope), _options);
    }

    public void MapIncoming(Envelope envelope, string json)
    {
        var node = JsonNode.Parse(json);
        MapIncoming(envelope, node);
    }

    public void MapIncoming(Envelope envelope, JsonNode? node)
    {
        mapIncoming(envelope, node, fallbackType: null);
    }

    // Inbound CloudEvents JSON is parsed into a JsonNode tree, then the "data"
    // child is materialized into the resolved message type via JsonNode.Deserialize.
    // That overload is reflection-based and carries IL2026/IL3050 — same AOT story
    // as the outbound Serialize calls above. Leaf suppression with a guide pointer
    // keeps the IMessageSerializer / IUnwrapsMetadataMessageSerializer interfaces
    // free of cascading [Requires*] annotations.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CloudEvents interop default deserializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CloudEvents interop default deserializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    private void mapIncoming(Envelope envelope, JsonNode? node, Type? fallbackType)
    {
        if (node == null) return;

        // *IF* SNS sent a message to SQS w/ CloudEvents
        if (node["Message"] != null)
        {
            var message = node["Message"];
            if (message!.GetValueKind() == JsonValueKind.String)
            {
                node = JsonNode.Parse(message.GetValue<string>());
            }
            else if (message.GetValueKind() == JsonValueKind.Object)
            {
                mapIncoming(envelope, node["Message"], fallbackType);
                return;
            }
        }

        if (node is JsonObject attributes)
        {
            readExtensionAttributes(envelope, attributes);
        }

        if (node!.TryGetValue<string>("tenantid", out var tenantid))
        {
            envelope.TenantId = tenantid;
        }

        if (node!.TryGetValue<string>("traceid", out var traceId))
        {
            envelope.CorrelationId = traceId;
        }

        if (node!.TryGetValue<string>("source", out var source))
        {
            envelope.Source = source;
        }

        if (node!.TryGetValue<DateTimeOffset>("time", out var time))
        {
            envelope.SentAt = time;
        }

        if (node!.TryGetValue<string>("id", out var raw))
        {
            if (Guid.TryParse(raw, out var id))
            {
                envelope.Id = id;
            }
            else
            {
                envelope.Id = Envelope.IdGenerator();
            }
        }

        if (node!.TryGetValue<string>("type", out var cloudEventType))
        {
            // Preserve the raw CloudEvent type on the envelope before resolution.
            // If resolution fails, the raw type survives for dead-letter persistence.
            envelope.MessageType = cloudEventType;

            // Resolve: try CloudEvent type alias first, then fall back to caller-provided
            // type (e.g. from DefaultIncomingMessage<T> via ReadFromData)
            var resolvedType = _handlers.TryFindMessageType(cloudEventType, out var messageType)
                ? messageType
                : fallbackType;

            if (resolvedType != null)
            {
                var data = node!["data"];
                if (data != null)
                {
                    envelope.Message = data.Deserialize(resolvedType, _options);
                }

                // Overwrite with the canonical Wolverine message type name
                envelope.MessageType = resolvedType.ToMessageTypeName();
            }
            else
            {
                throw new UnknownMessageTypeNameException($"Unknown message type alias '{cloudEventType}'. See the 'Message Routing' section of the dotnet run describe output to see the available .NET message types and their message type names");
            }
        }

        if (node!.TryGetValue<string>("datacontenttype", out var contentType))
        {
            if (contentType.StartsWith("application/json"))
            {
                envelope.ContentType = "application/json";
            }
            else
            {
                envelope.ContentType = contentType;
            }
        }
    }

    // GH-5080: lower case extension attributes come back as envelope headers. A header the transport
    // already mapped natively wins
    private static void readExtensionAttributes(Envelope envelope, JsonObject attributes)
    {
        foreach (var pair in attributes)
        {
            if (pair.Value is not JsonValue value || !IsExtensionAttribute(pair.Key)) continue;

            var text = value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
                _ => null
            };

            if (text != null)
            {
                envelope.Headers.TryAdd(pair.Key, text);
            }
        }
    }

    public string ContentType { get; } = "application/json";

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CloudEvents interop default serializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CloudEvents interop default serializer; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    public byte[] Write(Envelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new CloudEventsEnvelope(envelope), _options);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var node = JsonNode.Parse(envelope.Data);
        mapIncoming(envelope, node, fallbackType: messageType);

        return envelope.Message!;
    }

    public void Unwrap(Envelope envelope)
    {
        var node = JsonNode.Parse(envelope.Data);
        MapIncoming(envelope, node);
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotSupportedException();
    }
}

internal static class JsonNodeExtensions
{
    public static bool TryGetValue<T>(this JsonNode node, string nodeName, out T value)
    {
        var child = node[nodeName];
        if (child == null)
        {
            value = default!;
            return false;
        }

        value = child.GetValue<T>();
        return true;
    }
}

