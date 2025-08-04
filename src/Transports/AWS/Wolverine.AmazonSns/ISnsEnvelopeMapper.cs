using Amazon.SimpleNotificationService.Model;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.AmazonSns;

/// <summary>
/// Pluggable strategy for reading and writing data to Amazon SNS topics
/// </summary>
public interface ISnsEnvelopeMapper
{
    string BuildMessageBody(Envelope envelope);

    IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope);

    void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes);
}

internal class DefaultSnsEnvelopeMapper : ISnsEnvelopeMapper
{
    public string BuildMessageBody(Envelope envelope)
    {
        var data = EnvelopeSerializer.Serialize(envelope);
        return Convert.ToBase64String(data);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield return new KeyValuePair<string, MessageAttributeValue>(TransportConstants.ProtocolVersion,
            new MessageAttributeValue { StringValue = "1.0", DataType = "String" });
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody,
        IDictionary<string, MessageAttributeValue> attributes)
    {
        var buffer = Convert.FromBase64String(messageBody);
        EnvelopeSerializer.ReadEnvelopeData(envelope, buffer);
    }
}
