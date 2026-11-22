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

    /// <summary>
    /// GH-5768. A requeue, retry-letter or dead-letter copy of this message was accepted by the broker. The original
    /// is acked only after that, so a retry after a failed ack must not send a second copy.
    /// </summary>
    internal bool CopySent { get; set; }

    /// <summary>
    /// GH-5768. The original delivery has been acknowledged (or handed to a batched/cumulative ack). A Buffered
    /// listener acks every message as it reaches the in-memory queue, and Pulsar cannot redeliver an acked message.
    /// </summary>
    internal bool OriginalAcked { get; set; }
}
