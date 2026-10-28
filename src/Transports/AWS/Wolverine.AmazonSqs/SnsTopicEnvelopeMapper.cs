using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Amazon.SQS.Model;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs;

internal class SnsTopicEnvelopeMapper : ISqsEnvelopeMapper
{
    private readonly ISqsEnvelopeMapper _internalMessageMapper;

    public SnsTopicEnvelopeMapper(ISqsEnvelopeMapper internalMessageMapper)
    {
        _internalMessageMapper = internalMessageMapper;
    }

    public string BuildMessageBody(Envelope envelope)
    {
        return _internalMessageMapper.BuildMessageBody(envelope);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield return new KeyValuePair<string, MessageAttributeValue>(TransportConstants.ProtocolVersion,
            new MessageAttributeValue { StringValue = "1.0", DataType = "String" });
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        var body = readMessageBody(messageBody, out var snsMessageId);

        // The notification's own id is the one SNS keeps when it delivers the same notification to this queue
        // twice, under two different SQS message ids. SqsMessageIdentity derives the envelope id from it when
        // the message inside carries no Wolverine id of its own.
        if (envelope is AmazonSqsEnvelope sqs && !string.IsNullOrEmpty(snsMessageId))
        {
            sqs.SnsMessageId = snsMessageId;
        }

        _internalMessageMapper.ReadEnvelopeData(envelope, body, attributes);
    }

    private static string readMessageBody(string messageBody, out string? snsMessageId)
    {
        snsMessageId = null;

        try
        {
            var json = JsonSerializer.Deserialize(messageBody, SnsMessageMetadataJsonContext.Default.SnsMessageMetadata)
                       ?? throw new NullReferenceException();

            // A JSON body without a Message is not a notification -- a raw message whose own JSON happens to
            // have a MessageId property is not one -- so read it as the message itself, the same as a body
            // that is not JSON at all
            if (json.Message == null) return messageBody;

            snsMessageId = json.MessageId;
            return json.Message;
        }
        catch (JsonException)
        {
            return messageBody;
        }
    }

    internal class SnsMessageMetadata
    {
        public string Type { get; set; } = null!;
        public string MessageId { get; set; } = null!;
        public string TopicArn { get; set; } = null!;
        public string Message { get; set; } = null!;
        public string UnsubscribeURL { get; set; } = null!;
    }
}

/// <summary>
/// Source-generated JSON context for <see cref="SnsTopicEnvelopeMapper.SnsMessageMetadata"/>
/// — replaces the reflection-based JsonSerializer.Deserialize&lt;T&gt; overload so this
/// internal SNS-envelope detection stays AOT-clean. Same chunk N (NodeRecord) precedent.
/// </summary>
[JsonSerializable(typeof(SnsTopicEnvelopeMapper.SnsMessageMetadata))]
internal partial class SnsMessageMetadataJsonContext : JsonSerializerContext;
