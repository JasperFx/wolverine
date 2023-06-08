using Amazon.SQS.Model;
using Wolverine.Runtime.Serialization;

namespace Wolverine.AmazonSqs;

/// <summary>
/// Pluggable strategy for reading and writing data to Amazon SQS queues
/// </summary>
public interface ISqsEnvelopeMapper
{
    string BuildMessageBody(Envelope envelope);
    IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope);

    void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes);
}

internal class DefaultSqsEnvelopeMapper : ISqsEnvelopeMapper
{
    public string BuildMessageBody(Envelope envelope)
    {
        var data = EnvelopeSerializer.Serialize(envelope);
        return Convert.ToBase64String(data);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield break;
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        var buffer = Convert.FromBase64String(messageBody);
        EnvelopeSerializer.ReadEnvelopeData(envelope, buffer);
    }
}