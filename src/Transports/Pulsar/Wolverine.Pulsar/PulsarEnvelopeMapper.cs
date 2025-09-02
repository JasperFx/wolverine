using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

/// <summary>
/// Responsible for mapping incoming and outgoing Wolverine Envelope objects to the
/// Pulsar IMessage<ReadOnlySequence<byte>> or MessageMetadata object. Custom implementations of this can be used
/// to create interoperability with non-Wolverine applications through Pulsar
/// </summary>
public interface IPulsarEnvelopeMapper
{
    void MapIncomingToEnvelope(Envelope envelope, IMessage<ReadOnlySequence<byte>> incoming);
    void MapEnvelopeToOutgoing(Envelope envelope, MessageMetadata outgoing);
}

public class PulsarEnvelopeMapper : EnvelopeMapper<IMessage<ReadOnlySequence<byte>>, MessageMetadata>, IPulsarEnvelopeMapper
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
        return incoming.Properties.TryGetValue(key, out value);
    }

    protected override void writeIncomingHeaders(IMessage<ReadOnlySequence<byte>> incoming, Envelope envelope)
    {
        foreach (var pair in incoming.Properties) envelope.Headers[pair.Key] = pair.Value;
    }
}