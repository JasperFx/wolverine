using System.Text;
using Wolverine.Runtime.Interop;

namespace Wolverine.AmazonSns.Internal;

internal class CloudEventsSnsMapper : ISnsEnvelopeMapper
{
    private readonly CloudEventsMapper _inner;

    public CloudEventsSnsMapper(CloudEventsMapper inner)
    {
        _inner = inner;
    }

    public string BuildMessageBody(Envelope envelope)
    {
        return _inner.WriteToString(envelope);
    }

    public IEnumerable<KeyValuePair<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield break;
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue> attributes)
    {
        // TODO -- this could be more efficient of course
        envelope.Data = Encoding.UTF8.GetBytes(messageBody);
        envelope.Serializer = _inner;
    }
}