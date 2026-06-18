using Wolverine.ErrorHandling;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public static class KafkaRetryExtensions
{
    /// <summary>
    /// Non-blocking, tiered Kafka retry (GH-3148): on failure, produce the message to the next fixed-delay
    /// retry topic (<c>{source}.retry.{delay}</c>) and commit the source offset so the partition keeps
    /// flowing — no head-of-line blocking. A delayed consumer reprocesses the message once the tier delay
    /// elapses; after the last tier it lands in the existing Kafka dead letter queue.
    ///
    /// This is the DB-free non-blocking retry path. For database-backed apps, prefer
    /// <c>ScheduleRetry(...)</c> (durable, also non-blocking). Note that retried messages lose per-key
    /// ordering for that flow, and the delays are floors (consumer-side waiting), not exact.
    /// </summary>
    public static IAdditionalActions MoveToKafkaRetryTopic(this PolicyExpression expression, params TimeSpan[] delays)
    {
        if (delays == null || delays.Length == 0)
        {
            throw new ArgumentException("At least one retry delay tier is required", nameof(delays));
        }

        // Registered as a discoverable continuation source so the Kafka startup policy can validate it's
        // only applied to Kafka endpoints and auto-wire the delayed tier consumers. See GH-3148.
        return expression.ContinueWith(new MoveToKafkaRetryTopicContinuation(delays));
    }
}
