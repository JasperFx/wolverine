namespace Wolverine.Kafka;

/// <summary>
/// Describes a bounded, one-shot replay of a Kafka topic's history back through the normal Wolverine
/// handler pipeline (GH-3147). The replay uses a throwaway <c>Assign()</c>-based consumer and never
/// commits to the live consumer group, so steady-state consumption is untouched.
///
/// Start defaults to the beginning of each partition (earliest), end defaults to the current high-water
/// mark ("now"). Replayed messages are re-handled, so handlers should be idempotent.
/// </summary>
public class KafkaReplayRequest
{
    /// <summary>The topic to replay.</summary>
    public required string Topic { get; init; }

    /// <summary>Start the replay at this absolute offset on every partition. Mutually exclusive with <see cref="FromTimestamp"/>.</summary>
    public long? FromOffset { get; init; }

    /// <summary>Start the replay at the first record whose timestamp is at or after this instant (resolved per partition via OffsetsForTimes).</summary>
    public DateTimeOffset? FromTimestamp { get; init; }

    /// <summary>Stop the replay at this absolute offset (exclusive) on every partition. Mutually exclusive with <see cref="ToTimestamp"/>.</summary>
    public long? ToOffset { get; init; }

    /// <summary>Stop the replay at the first record whose timestamp is at or after this instant. Mutually exclusive with <see cref="ToOffset"/>.</summary>
    public DateTimeOffset? ToTimestamp { get; init; }

    /// <summary>Restrict the replay to these partitions. Null or empty replays all partitions.</summary>
    public int[]? Partitions { get; init; }
}

/// <summary>
/// Outcome of a <see cref="KafkaReplayRequest"/>.
/// </summary>
public class KafkaReplayResult
{
    /// <summary>Number of records fed back through the handler pipeline.</summary>
    public long RecordsReplayed { get; init; }

    /// <summary>Number of partitions that had records within the requested window.</summary>
    public int PartitionsReplayed { get; init; }
}
