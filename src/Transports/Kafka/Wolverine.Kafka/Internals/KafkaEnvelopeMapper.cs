using System.Text;
using Confluent.Kafka;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

internal class KafkaEnvelopeMapper : EnvelopeMapper<Message<string, byte[]>, Message<string, byte[]>>, IKafkaEnvelopeMapper
{
    public KafkaEnvelopeMapper(Endpoint endpoint) : base(endpoint)
    {

    }

    protected override void writeOutgoingHeader(Message<string, byte[]> outgoing, string key, string value)
    {
        outgoing.Headers.Add(key, Encoding.Default.GetBytes(value));
    }

    protected override bool tryReadIncomingHeader(Message<string, byte[]> incoming, string key, out string value)
    {
        if (incoming.Headers.TryGetLastBytes(key, out var bytes))
        {
            value = Encoding.Default.GetString(bytes);
            return true;
        }

        value = default!;
        return false;
    }
}