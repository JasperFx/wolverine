using System.Text;
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
        _inner.MapIncoming(envelope, messageBody);
    }
}