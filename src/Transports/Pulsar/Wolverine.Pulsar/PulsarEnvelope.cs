using System.Buffers;
using DotPulsar.Abstractions;

namespace Wolverine.Pulsar;

public class PulsarEnvelope : Envelope
{
    public PulsarEnvelope(IMessage<ReadOnlySequence<byte>> messageData)
    {
        MessageData = messageData;
    }

    public IMessage<ReadOnlySequence<byte>> MessageData { get; }
}