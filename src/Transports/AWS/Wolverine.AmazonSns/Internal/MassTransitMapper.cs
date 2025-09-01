using System.Text;
using Amazon.SimpleNotificationService.Model;
using Wolverine.AmazonSqs;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.AmazonSns.Internal;

internal class MassTransitMapper : ISnsEnvelopeMapper
{
    private readonly IMassTransitInteropEndpoint _endpoint;
    private MassTransitJsonSerializer _serializer;

    public MassTransitMapper(IMassTransitInteropEndpoint endpoint)
    {
        _endpoint = endpoint;
        _serializer = new MassTransitJsonSerializer(endpoint);
    }

    public MassTransitJsonSerializer Serializer => _serializer;

    public string BuildMessageBody(Envelope envelope)
    {
        return Encoding.UTF8.GetString(_serializer.Write(envelope));
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield break;
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        // TODO -- this could be more efficient of course
        envelope.Data = Encoding.UTF8.GetBytes(messageBody);
        envelope.Serializer = _serializer;
    }

}