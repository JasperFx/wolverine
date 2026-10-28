using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using Wolverine.Runtime.Interop;

namespace Wolverine.AmazonSqs.Internal;

internal class CloudEventsSqsMapper : ISqsEnvelopeMapper
{
    private readonly CloudEventsMapper _inner;

    public CloudEventsSqsMapper(CloudEventsMapper inner)
    {
        _inner = inner;
    }

    public override string ToString() => "Cloud Events";

    public string BuildMessageBody(Envelope envelope)
    {
        return _inner.WriteToString(envelope);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield break;
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        // TODO -- this could be more efficient of course
        envelope.Data = Encoding.UTF8.GetBytes(messageBody);
        envelope.Serializer = _inner;

        var cloudEvent = unwrapSnsNotification(envelope, JsonNode.Parse(messageBody));

        var idBeforeMapping = envelope.Id;
        _inner.MapIncoming(envelope, cloudEvent);

        // CloudEvents ids are strings, and CloudEventsMapper gives an event whose id is not a UUID a brand new
        // Envelope.Id -- a different one on every delivery, which the durable inbox cannot recognize as a
        // redelivery. Leave the id as it was instead, so the listener derives it from the broker's identity of
        // the message the same way it does for a message that carries no id at all.
        if (!hasUuidId(cloudEvent))
        {
            envelope.Id = idBeforeMapping;
        }
    }

    /// <summary>
    /// An SNS subscription without raw message delivery wraps the CloudEvent in a notification. CloudEventsMapper
    /// unwraps that too; doing it here first is what makes the notification's own <c>MessageId</c> visible.
    /// </summary>
    private static JsonNode? unwrapSnsNotification(Envelope envelope, JsonNode? node)
    {
        while (node is JsonObject notification && notification["Message"] is { } message)
        {
            if (envelope is AmazonSqsEnvelope sqs && notification["MessageId"] is JsonValue messageId &&
                messageId.TryGetValue<string>(out var snsMessageId) && snsMessageId.Length > 0)
            {
                sqs.SnsMessageId = snsMessageId;
            }

            node = message.GetValueKind() switch
            {
                JsonValueKind.String => JsonNode.Parse(message.GetValue<string>()),
                JsonValueKind.Object => message,
                _ => node
            };

            if (ReferenceEquals(node, notification)) break;
        }

        return node;
    }

    private static bool hasUuidId(JsonNode? cloudEvent)
    {
        return cloudEvent?["id"] is JsonValue id && id.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out _);
    }
}
