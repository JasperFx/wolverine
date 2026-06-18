namespace Wolverine.Kafka;

/// <summary>
/// Controls how a Kafka listener commits consumer offsets back to the broker. See GH-3150.
/// </summary>
public enum CommitMode
{
    /// <summary>
    /// Synchronously commit each message's own offset as it finishes processing. Strict
    /// at-least-once with minimal reprocessing on restart, but the slowest option — every message
    /// pays for a synchronous broker round trip.
    /// </summary>
    PerMessage,

    /// <summary>
    /// (Default) Store each successfully processed offset locally (<c>EnableAutoOffsetStore=false</c> +
    /// <c>StoreOffset</c>) and let Kafka's background auto-committer flush them on
    /// <c>AutoCommitIntervalMs</c>. Non-blocking, at-least-once, and the idiomatic high-throughput
    /// Kafka model.
    /// </summary>
    StoreThenAutoFlush,

    /// <summary>
    /// Wolverine commits the contiguous offset watermark after every N successfully processed
    /// messages. Never commits ahead of the lowest in-flight offset.
    /// </summary>
    BatchCount,

    /// <summary>
    /// Wolverine commits the contiguous offset watermark once at least the configured interval has
    /// elapsed since the previous commit. Never commits ahead of the lowest in-flight offset.
    /// </summary>
    BatchInterval
}
