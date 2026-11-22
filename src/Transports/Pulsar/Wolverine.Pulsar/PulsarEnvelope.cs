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

    // GH-5813: how this delivery is settled, claimed once. A Buffered listener acks from its receiver's ack block
    // while the handler may already have failed, so an ack and a native redelivery can race for the same delivery.
    private const int Unsettled = 0;
    private const int SettledByAck = 1;
    private const int SettledByNativeRedelivery = 2;
    private int _settlement;

    /// <summary>
    /// GH-5813. Claim this delivery for an acknowledgment. False when a native redelivery already claimed it: Pulsar
    /// is redelivering the message, and an ack now would either cancel that redelivery or follow a redelivered copy.
    /// A retried ack keeps its claim.
    /// </summary>
    internal bool TryClaimAck()
    {
        return Interlocked.CompareExchange(ref _settlement, SettledByAck, Unsettled) != SettledByNativeRedelivery;
    }

    /// <summary>
    /// GH-5813. Claim this delivery for a native redelivery. False once an acknowledgment claimed it, even one still
    /// on its way to the broker: Pulsar cannot redeliver an acked message, so the caller sends a copy instead.
    /// </summary>
    internal bool TryClaimNativeRedelivery()
    {
        return Interlocked.CompareExchange(ref _settlement, SettledByNativeRedelivery, Unsettled) != SettledByAck;
    }
}
