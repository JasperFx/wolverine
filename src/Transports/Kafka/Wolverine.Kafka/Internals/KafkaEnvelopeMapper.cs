using System.Text;
using Confluent.Kafka;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

public class KafkaEnvelopeMapper : EnvelopeMapper<Message<string, byte[]>, Message<string, byte[]>>, IKafkaEnvelopeMapper
{
    public KafkaEnvelopeMapper(Endpoint endpoint) : base(endpoint)
    {

    }

    // writeIncomingHeaders below copies every incoming header verbatim (same keys, same UTF8
    // decode as tryReadIncomingHeader), so the typed reserved-header readers can reuse those
    // already-decoded values instead of re-scanning and re-decoding the Kafka header list per
    // property (GH-3490).
    protected override bool preferCopiedIncomingHeaders => true;

    protected override void writeOutgoingHeader(Message<string, byte[]> outgoing, string key, string value)
    {
        outgoing.Headers.Add(key, Encoding.UTF8.GetBytes(value));
    }

    protected override bool tryReadIncomingHeader(Message<string, byte[]> incoming, string key, out string value)
    {
        if (incoming.Headers.TryGetLastBytes(key, out var bytes))
        {
            value = Encoding.UTF8.GetString(bytes);
            return true;
        }

        value = default!;
        return false;
    }

    protected override void writeIncomingHeaders(Message<string, byte[]> incoming, Envelope envelope)
    {
        if (incoming.Headers == null) return;
        foreach (var header in incoming.Headers)
        {
            var bytes = header.GetValueBytes();
            envelope.Headers[header.Key] = bytes != null ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

}