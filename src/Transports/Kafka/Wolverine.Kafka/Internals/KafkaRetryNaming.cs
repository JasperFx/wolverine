namespace Wolverine.Kafka.Internals;

/// <summary>
/// Naming + header conventions for the non-blocking tiered retry topics (GH-3148). Tier topics are named
/// <c>{source}.retry.{delay}</c> (e.g. <c>orders.retry.1s</c>, <c>orders.retry.30s</c>, <c>orders.retry.5m</c>).
/// </summary>
internal static class KafkaRetryNaming
{
    // Header carrying the original source topic so a tiered record can be re-produced/escalated correctly.
    public const string SourceTopicHeader = "wolverine-kafka-retry-source";

    // Header carrying the 0-based tier index the record currently sits on.
    public const string TierHeader = "wolverine-kafka-retry-tier";

    // Header carrying the cumulative attempt count across the source + retry tiers.
    public const string AttemptHeader = "wolverine-kafka-retry-attempt";

    // Header carrying the UTC ticks of the first failure, for diagnostics/DLQ metadata.
    public const string FirstFailedHeader = "wolverine-kafka-retry-first-failed";

    public const string ExceptionTypeHeader = "wolverine-kafka-retry-exception-type";
    public const string ExceptionMessageHeader = "wolverine-kafka-retry-exception-message";

    /// <summary>
    /// Compact, human-readable representation of a delay: whole hours as <c>{n}h</c>, whole minutes as
    /// <c>{n}m</c>, otherwise <c>{n}s</c>. (e.g. 1s, 30s, 5m, 1h, 90s.)
    /// </summary>
    public static string CompactDelay(TimeSpan delay)
    {
        var totalSeconds = (long)Math.Round(delay.TotalSeconds);
        if (totalSeconds <= 0)
        {
            return "0s";
        }

        if (totalSeconds % 3600 == 0)
        {
            return $"{totalSeconds / 3600}h";
        }

        if (totalSeconds % 60 == 0)
        {
            return $"{totalSeconds / 60}m";
        }

        return $"{totalSeconds}s";
    }

    public static string RetryTopicName(string sourceTopic, TimeSpan delay)
    {
        return $"{sourceTopic}.retry.{CompactDelay(delay)}";
    }
}
