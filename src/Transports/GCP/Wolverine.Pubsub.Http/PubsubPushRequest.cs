using System.Text.Json;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Wolverine.Pubsub.Http;

/// <summary>
///     GH-5016. The body of a wrapped Pub/Sub push request:
///     <c>{ "message": { "data", "attributes", "messageId", "publishTime", "orderingKey" }, "subscription", "deliveryAttempt" }</c>
/// </summary>
internal class PubsubPushRequest
{
    private PubsubPushRequest(PubsubMessage message, string subscription, int? deliveryAttempt)
    {
        Message = message;
        Subscription = subscription;
        DeliveryAttempt = deliveryAttempt;
    }

    public PubsubMessage Message { get; }

    /// <summary>
    ///     The full subscription name, <c>projects/{project}/subscriptions/{subscription}</c>
    /// </summary>
    public string Subscription { get; }

    /// <summary>
    ///     GH-5048. Which delivery of the message this is, starting at 1. Pub/Sub only sends it when the subscription
    ///     has a dead letter policy, so null otherwise
    /// </summary>
    public int? DeliveryAttempt { get; }

    /// <summary>
    ///     Null when the body is not a push request
    /// </summary>
    public static async Task<PubsubPushRequest?> ReadAsync(Stream body, CancellationToken cancellation)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var json) ||
                json.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var message = new PubsubMessage();

            try
            {
                if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                {
                    message.Data = ByteString.FromBase64(data.GetString());
                }
            }
            catch (FormatException)
            {
                return null;
            }

            if (json.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
            {
                foreach (var attribute in attributes.EnumerateObject())
                {
                    if (attribute.Value.ValueKind == JsonValueKind.String)
                    {
                        message.Attributes[attribute.Name] = attribute.Value.GetString();
                    }
                }
            }

            // The push format repeats these under both names
            message.MessageId = stringOf(json, "messageId") ?? stringOf(json, "message_id") ?? string.Empty;
            message.OrderingKey = stringOf(json, "orderingKey") ?? string.Empty;

            var publishTime = stringOf(json, "publishTime") ?? stringOf(json, "publish_time");
            if (publishTime != null && DateTimeOffset.TryParse(publishTime, out var published))
            {
                message.PublishTime = Timestamp.FromDateTimeOffset(published);
            }

            return new PubsubPushRequest(message, stringOf(root, "subscription") ?? string.Empty,
                deliveryAttemptOf(root));
        }
    }

    // A JSON number in the push body, but accept it as a string too
    private static int? deliveryAttemptOf(JsonElement root)
    {
        if (!root.TryGetProperty("deliveryAttempt", out var value)) return null;

        var attempt = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => (int?)null
        };

        return attempt > 0 ? attempt : null;
    }

    private static string? stringOf(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
