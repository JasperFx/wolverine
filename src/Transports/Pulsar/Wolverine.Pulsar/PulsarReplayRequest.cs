using DotPulsar;

namespace Wolverine.Pulsar;

/// <summary>
/// Describes a bounded, one-shot replay of a Pulsar topic's history back through the normal Wolverine
/// handler pipeline (GH-3184). The replay uses a throwaway, non-durable <c>Reader</c> cursor and never
/// touches any live durable subscription, so steady-state consumption is undisturbed.
///
/// Start defaults to the earliest retained message in the topic, end defaults to the topic's last
/// message at the moment the replay begins ("now") so the replay never tails live traffic. Replayed
/// messages are re-handled, so handlers should be idempotent.
/// </summary>
public class PulsarReplayRequest
{
    /// <summary>
    /// The native Pulsar topic path to replay, e.g. <c>persistent://public/default/orders</c>.
    /// </summary>
    public required string Topic { get; init; }

    /// <summary>
    /// Start the replay at this <see cref="MessageId"/> (inclusive). Mutually exclusive with
    /// <see cref="FromTimestamp"/>. When neither is set the replay starts at the earliest retained message.
    /// </summary>
    public MessageId? FromMessageId { get; init; }

    /// <summary>
    /// Start the replay at the first message whose publish time is at or after this instant. Mutually
    /// exclusive with <see cref="FromMessageId"/>.
    /// </summary>
    public DateTimeOffset? FromTimestamp { get; init; }

    /// <summary>
    /// Stop the replay at this <see cref="MessageId"/> (inclusive). Mutually exclusive with
    /// <see cref="ToTimestamp"/>. When neither is set the replay stops at the topic's last message as of
    /// the moment the replay began.
    /// </summary>
    public MessageId? ToMessageId { get; init; }

    /// <summary>
    /// Stop the replay at the last message whose publish time is at or before this instant. Mutually
    /// exclusive with <see cref="ToMessageId"/>.
    /// </summary>
    public DateTimeOffset? ToTimestamp { get; init; }
}

/// <summary>
/// Outcome of a <see cref="PulsarReplayRequest"/>.
/// </summary>
public class PulsarReplayResult
{
    /// <summary>Number of messages fed back through the handler pipeline.</summary>
    public long MessagesReplayed { get; init; }
}
