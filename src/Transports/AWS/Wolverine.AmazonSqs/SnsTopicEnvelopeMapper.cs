using System.Text.Json.Nodes;
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
        var body = JsonNode.Parse(messageBody)?["Message"]?.ToString();
        _internalMessageMapper.ReadEnvelopeData(envelope, body, attributes);
    }
}
