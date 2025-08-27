using System.Text.Json;
using System.Text.Json.Nodes;
using Wolverine.Runtime.Handlers;
using Wolverine.Util;

namespace Wolverine.Runtime.Interop;

internal class CloudEventsMapper
{
    private readonly HandlerGraph _handlers;
    private readonly JsonSerializerOptions _options;

    public CloudEventsMapper(HandlerGraph handlers, JsonSerializerOptions options)
    {
        _handlers = handlers;
        _options = options;
    }

    public void MapIncoming(Envelope envelope, string json)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return;

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

        if (node.TryGetValue<Guid>("id", out var id))
        {
            envelope.Id = id;
        }

        if (node.TryGetValue<string>("type", out var cloudEventType))
        {
            if (_handlers.TryFindMessageTypeForCloudEvent(cloudEventType, out var messageType))
            {
                var data = node["data"];
                if (data != null)
                {
                    envelope.Message = data.Deserialize(messageType, _options);
                }

                envelope.MessageType = messageType.ToMessageTypeName();
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

