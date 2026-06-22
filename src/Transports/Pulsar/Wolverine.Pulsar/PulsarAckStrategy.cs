namespace Wolverine.Pulsar;

/// <summary>
/// How a Pulsar listener acknowledges successfully processed messages. The Pulsar analogue of the
/// Kafka transport's commit-mode choice (#3150).
/// </summary>
public enum PulsarAckStrategy
{
    /// <summary>
    /// Acknowledge each message individually as soon as it completes (default, current behavior).
    /// </summary>
    Individual,

    /// <summary>
    /// Acknowledge cumulatively — a single ack confirms every message up to and including a point in
    /// the subscription. Reduces broker chatter on high-volume ordered subscriptions. Only valid for
    /// Exclusive / Failover subscriptions. Wolverine only advances the cumulative ack to the highest
    /// contiguous-completed message, so a cumulative ack never confirms a still-in-flight message.
    /// </summary>
    Cumulative,

    /// <summary>
    /// Acknowledge messages individually but in batches, flushed when a batch size is reached or a
    /// time interval elapses. Reduces broker chatter without the ordering constraints of cumulative
    /// ack; safe for every subscription type.
    /// </summary>
    Batched
}
