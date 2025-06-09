using System.Text.Json;
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
        var body = ReadMessageBody(messageBody);
        _internalMessageMapper.ReadEnvelopeData(envelope, body, attributes);
    }

    private static string ReadMessageBody(string messageBody)
    {
        try
        {
            var json = JsonSerializer.Deserialize<SnsMessageMetadata>(messageBody) 
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
        public string Type { get; set; }
        public string MessageId { get; set; }
        public string TopicArn { get; set; }
        public string Message { get; set; }
        public string UnsubscribeURL { get; set; }
    }
}
