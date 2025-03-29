using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

public class PulsarEnvelopeMapper : EnvelopeMapper<IMessage<ReadOnlySequence<byte>>, MessageMetadata>
{
    public PulsarEnvelopeMapper(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint)
    {
    }

    protected override void writeOutgoingHeader(MessageMetadata outgoing, string key, string value)
    {
        outgoing[key] = value;
    }

    protected override bool tryReadIncomingHeader(IMessage<ReadOnlySequence<byte>> incoming, string key,
        out string? value)
    {
        if (key == EnvelopeConstants.AttemptsKey && incoming.Properties.TryGetValue(PulsarEnvelopeConstants.ReconsumeTimes, out value))
        {
            // dirty hack, handler increments Attempt field
            int val = int.Parse(value);
            val--;
            value = val.ToString();
            return true;
        }
        return incoming.Properties.TryGetValue(key, out value);
    }

    protected override void writeIncomingHeaders(IMessage<ReadOnlySequence<byte>> incoming, Envelope envelope)
    {
        foreach (var pair in incoming.Properties)
        {
            envelope.Headers[pair.Key] = pair.Value;

            // doesn't work, it gets overwritten in next step - fix in tryReadIncomingHeader
            //if (pair.Key == PulsarEnvelopeConstants.ReconsumeTimes)
            //{
            //    envelope.Headers[EnvelopeConstants.AttemptsKey] = pair.Value;
            //    envelope.Attempts = int.Parse(pair.Value);
            //}
        }
    }
}