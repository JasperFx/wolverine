using System.Text.Json;
using Amazon.SQS.Model;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.AmazonSqs;

internal class RawJsonSqsEnvelopeMapper : ISqsEnvelopeMapper
{
    private readonly Type _defaultMessageType;
    private readonly JsonSerializerOptions _serializerOptions;

    public RawJsonSqsEnvelopeMapper(Type defaultMessageType, JsonSerializerOptions serializerOptions)
    {
        _defaultMessageType = defaultMessageType;
        _serializerOptions = serializerOptions;
    }

    public string BuildMessageBody(Envelope envelope)
    {
        return JsonSerializer.Serialize(
            envelope.Message,
            _defaultMessageType,
            _serializerOptions);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield return new KeyValuePair<string, MessageAttributeValue>(TransportConstants.ProtocolVersion,
            new MessageAttributeValue { StringValue = "1.0", DataType = "String" });
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        // assuming json serialized message
        envelope.MessageType = _defaultMessageType.ToMessageTypeName();
        envelope.Message = JsonSerializer.Deserialize(
            messageBody,
            _defaultMessageType,
            _serializerOptions);
    }
}