using System.Text;
using Dekaf.Consumer;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

public class KafkaEnvelopeMapper : EnvelopeMapper<ConsumeResult<string, byte[]>, OutgoingKafkaRecord>, IKafkaEnvelopeMapper
{
    public KafkaEnvelopeMapper(Endpoint endpoint) : base(endpoint)
    {

    }

    // writeIncomingHeaders below copies every incoming header verbatim (same keys, same UTF8
    // decode as tryReadIncomingHeader), so the typed reserved-header readers can reuse those
    // already-decoded values instead of re-scanning and re-decoding the Kafka header list per
    // property (GH-3490).
    protected override bool preferCopiedIncomingHeaders => true;

    protected override void writeOutgoingHeader(OutgoingKafkaRecord outgoing, string key, string value)
    {
        outgoing.Headers.Add(key, Encoding.UTF8.GetBytes(value));
    }

    protected override bool tryReadIncomingHeader(ConsumeResult<string, byte[]> incoming, string key, out string value)
    {
        // Last occurrence of a repeated key wins, matching Confluent's TryGetLastBytes semantics.
        // GetValueAsString copies out of the consumer's pooled buffer, so the value stays valid
        // after the consumer advances.
        var headers = incoming.Headers;
        for (var i = headers.Count - 1; i >= 0; i--)
        {
            if (headers[i].Key == key && !headers[i].IsValueNull)
            {
                value = headers[i].GetValueAsString()!;
                return true;
            }
        }

        value = default!;
        return false;
    }

    protected override void writeIncomingHeaders(ConsumeResult<string, byte[]> incoming, Envelope envelope)
    {
        foreach (var header in incoming.Headers)
        {
            envelope.Headers[header.Key] = header.IsValueNull ? null : header.GetValueAsString();
        }
    }
}
