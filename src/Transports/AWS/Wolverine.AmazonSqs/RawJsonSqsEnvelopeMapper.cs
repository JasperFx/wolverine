using System.Diagnostics.CodeAnalysis;
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

    public override string ToString() => "Raw JSON";

    // Reflection-based JsonSerializer.Serialize/Deserialize over a runtime-
    // resolved user message type. Same chunk D / E pattern (default JSON
    // serializer for IMessageSerializer): AOT-clean apps supply their own
    // ISqsEnvelopeMapper backed by a JsonSerializerContext (or pre-register
    // a JsonTypeInfo for the message type). See AOT guide.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Raw JSON SQS mapper uses reflection-based STJ over a runtime-resolved message type; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Raw JSON SQS mapper uses reflection-based STJ over a runtime-resolved message type; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
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

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Raw JSON SQS mapper uses reflection-based STJ over a runtime-resolved message type; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Raw JSON SQS mapper uses reflection-based STJ over a runtime-resolved message type; AOT consumers supply a JsonSerializerContext-backed mapper. See AOT guide.")]
    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        // assuming json serialized message
        envelope.MessageType = _defaultMessageType.ToMessageTypeName();
        envelope.ContentType = EnvelopeConstants.JsonContentType;
        envelope.Message = JsonSerializer.Deserialize(
            messageBody,
            _defaultMessageType,
            _serializerOptions);
    }
}