using Wolverine.ErrorHandling;
using Wolverine.Pulsar.ErrorHandling;

namespace Wolverine.Pulsar;

public static class PulsarRetryExtensions
{
    /// <summary>
    /// Route a failed Pulsar message through Pulsar's native tiered retry-letter topic with the supplied
    /// per-tier delays, then to the dead-letter topic once every tier is exhausted (GH-3182). The Pulsar
    /// analogue of <c>MoveToKafkaRetryTopic</c>, exposed as a first-class, discoverable error policy.
    ///
    /// Each delay is one retry tier: on the first failure the message is redelivered after
    /// <c>delays[0]</c>, on the second after <c>delays[1]</c>, and so on; after the last tier it lands in
    /// the dead-letter topic. The delays are applied to every Pulsar listener endpoint at startup, which
    /// provisions the retry-letter producer + consumer and the DLQ.
    ///
    /// Pulsar message delaying only works on <see cref="DotPulsar.SubscriptionType.Shared"/> /
    /// <see cref="DotPulsar.SubscriptionType.KeyShared"/> subscriptions; a startup warning is emitted for
    /// any Pulsar listener using another subscription type (where this degrades to an inline retry).
    /// </summary>
    public static IAdditionalActions MoveToPulsarRetryTopic(this PolicyExpression expression, params TimeSpan[] delays)
    {
        if (delays == null || delays.Length == 0)
        {
            throw new ArgumentException("At least one retry delay tier is required", nameof(delays));
        }

        return expression.ContinueWith(new MoveToPulsarRetryTopicContinuation(delays));
    }
}
