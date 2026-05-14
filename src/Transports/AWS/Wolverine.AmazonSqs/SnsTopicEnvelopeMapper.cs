using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Amazon.SQS.Model;
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
        var body = ReadMessageBody(messageBody);
        _internalMessageMapper.ReadEnvelopeData(envelope, body, attributes);
    }

    private static string ReadMessageBody(string messageBody)
    {
        try
        {
            var json = JsonSerializer.Deserialize(messageBody, SnsMessageMetadataJsonContext.Default.SnsMessageMetadata)
                       ?? throw new NullReferenceException();
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
