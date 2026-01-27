using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using MassTransit;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
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
        
        TenantId = envelope.TenantId;
        Data = envelope.Message;
        
        // Doesn't always apply in Wolverine, so ¯\_(ツ)_/¯
        Topic = envelope.TopicName ?? envelope.GroupId;

        Id = envelope.Id;
        TraceId = envelope.CorrelationId;
        Source = envelope.Source;
        
        // This is the Wolverine string that aliases the message type
        Type = envelope.MessageType;

        Time = envelope.SentAt.ToString("O");

        TraceParent = envelope.ParentId;
    }
    
    [JsonPropertyName("topic")]
    public string Topic { get; set; }
    
    [JsonPropertyName("tenantid")]
    public string TenantId { get; set; }
    
    [JsonPropertyName("traceid")]
    public string TraceId { get; set; }
    
    [JsonPropertyName("tracestate")]
    public string TraceState { get; set; }

    [JsonPropertyName("data")]
    public object Data { get; set; }
    
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("specversion")]
    public string SpecVersion { get; set; } = "1.0";

    [JsonPropertyName("datacontenttype")]
    public string DataContentType { get; set; } = "application/json; charset=utf-8";
    
    [JsonPropertyName("source")]
    public string Source { get; set; }
    
    [JsonPropertyName("type")]
    public string Type { get; set; }
    
    [JsonPropertyName("time")]
    public string Time { get; set; }
    
    [JsonPropertyName("traceparent")]
    public string TraceParent { get; set; }
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

    public string WriteToString(Envelope envelope)
    {
        return JsonSerializer.Serialize(new CloudEventsEnvelope(envelope), _options);
    }

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
        if (node == null) return;

        // *IF* SNS sent a message to SQS w/ CloudEvents
        if (node["Message"] != null)
        {
            var message = node["Message"];
            if (message.GetValueKind() == JsonValueKind.String)
            {
                node = JsonNode.Parse(message.GetValue<string>());
            }
            else if (message.GetValueKind() == JsonValueKind.Object)
            {
                MapIncoming(envelope, node["Message"]);
                return;
            }
        }

        if (node.TryGetValue<string>("tenantid", out var tenantid))
        {
            envelope.TenantId = tenantid;
        }

        if (node.TryGetValue<string>("traceid", out var traceId))
        {
            envelope.CorrelationId = traceId;
        }

        if (node.TryGetValue<string>("source", out var source))
        {
            envelope.Source = source;
        }

        if (node.TryGetValue<DateTimeOffset>("time", out var time))
        {
            envelope.SentAt = time;
        }

        if (node.TryGetValue<string>("id", out var raw))
        {
            if (Guid.TryParse(raw, out var id))
            {
                envelope.Id = id;
            }
            else
            {
                envelope.Id = NewId.NextSequentialGuid();
            }
        }

        if (node.TryGetValue<string>("type", out var cloudEventType))
        {
            if (_handlers.TryFindMessageType(cloudEventType, out var messageType))
            {
                var data = node["data"];
                if (data != null)
                {
                    envelope.Message = data.Deserialize(messageType, _options);
                }

                envelope.MessageType = messageType.ToMessageTypeName();
            }
            else
            {
                throw new UnknownMessageTypeNameException($"Unknown message type alias '{cloudEventType}'. See the 'Message Routing' section of the dotnet run describe output to see the available .NET message types and their message type names");
            }
        }

        if (node.TryGetValue<string>("datacontenttype", out var contentType))
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

    public string ContentType { get; } = "application/json";
    
    public byte[] Write(Envelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new CloudEventsEnvelope(envelope), _options);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var node = JsonNode.Parse(envelope.Data);
        MapIncoming(envelope, node);

        return envelope.Message;
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
            value = default;
            return false;
        }

        value = child.GetValue<T>();
        return true;
    }
}

