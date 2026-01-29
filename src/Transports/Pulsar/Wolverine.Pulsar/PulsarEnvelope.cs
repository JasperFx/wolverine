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

    /// <summary>
    /// Indicates if this message came from the retry topic consumer
    /// </summary>
    public bool IsFromRetryConsumer { get; set; }
}