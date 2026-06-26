using System.Diagnostics;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Error-handling continuation for non-blocking tiered retry topics (GH-3148). On failure it produces the
/// message to the next retry tier's topic (<c>{source}.retry.{delay}</c>) with retry metadata headers and
/// commits the source offset so the partition keeps flowing — no head-of-line blocking. After the last
/// tier is exhausted it falls back to the existing Kafka dead letter queue.
/// </summary>
internal sealed class MoveToKafkaRetryTopicContinuation : UserDefinedContinuation
{
    private readonly TimeSpan[] _delays;
    private readonly Exception? _exception;

    public MoveToKafkaRetryTopicContinuation(TimeSpan[] delays) : this(delays, null)
    {
    }

    private MoveToKafkaRetryTopicContinuation(TimeSpan[] delays, Exception? exception)
        : base($"Move to Kafka retry topic ({delays.Length} tiers)")
    {
        _delays = delays;
        _exception = exception;
    }

    public TimeSpan[] Delays => _delays;

    // Build carries the live exception for the actual execution. Crucially, this policy only ever acts on
    // Kafka listeners: anything arriving over another transport falls back to a normal inline retry, so the
    // Kafka retry-topic routing can never be applied to a different transport. See GH-3148.
    public override IContinuation Build(Exception ex, Envelope envelope)
    {
        if (envelope.Listener is KafkaListener or KafkaTopicGroupListener)
        {
            return new MoveToKafkaRetryTopicContinuation(_delays, ex);
        }

        return RetryInlineContinuation.Instance;
    }

    public override async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        var envelope = lifecycle.Envelope!;
        var exception = _exception ?? envelope.Failure ?? new Exception("Unknown failure");

        var currentTier = ReadIntHeader(envelope, KafkaRetryNaming.TierHeader, -1);
        var nextTier = currentTier + 1;

        // Exhausted all tiers -> existing Kafka DLQ.
        if (nextTier >= _delays.Length)
        {
            await lifecycle.MoveToDeadLetterQueueAsync(exception);
            return;
        }

        var sourceTopic = ReadStringHeader(envelope, KafkaRetryNaming.SourceTopicHeader) ?? envelope.TopicName;
        if (sourceTopic == null)
        {
            // Can't derive the retry topic without a source topic; fall back to the DLQ.
            await lifecycle.MoveToDeadLetterQueueAsync(exception);
            return;
        }

        var attempt = ReadIntHeader(envelope, KafkaRetryNaming.AttemptHeader, 1) + 1;
        var firstFailed = ReadStringHeader(envelope, KafkaRetryNaming.FirstFailedHeader)
                          ?? now.UtcTicks.ToString(CultureInfo.InvariantCulture);

        var transport = runtime.Options.Transports.GetOrCreate<KafkaTransport>();
        var retryTopicName = KafkaRetryNaming.RetryTopicName(sourceTopic, _delays[nextTier]);
        var retryTopic = transport.Topics[retryTopicName];
        retryTopic.EnsureEnvelopeMapper(runtime);

        var message = await retryTopic.EnvelopeMapper!.CreateMessage(envelope);
        message.Headers ??= new Headers();

        StampHeader(message, KafkaRetryNaming.SourceTopicHeader, sourceTopic);
        StampHeader(message, KafkaRetryNaming.TierHeader, nextTier.ToString(CultureInfo.InvariantCulture));
        StampHeader(message, KafkaRetryNaming.AttemptHeader, attempt.ToString(CultureInfo.InvariantCulture));
        StampHeader(message, KafkaRetryNaming.FirstFailedHeader, firstFailed);
        StampHeader(message, KafkaRetryNaming.ExceptionTypeHeader, exception.GetType().FullName ?? "Unknown");
        StampHeader(message, KafkaRetryNaming.ExceptionMessageHeader, exception.Message);

        try
        {
            using var producer = transport.CreateProducer(retryTopic.GetEffectiveProducerConfig());
            await producer.ProduceAsync(retryTopicName, message);
            producer.Flush();

            // Commit the *source* offset so the partition advances past this message.
            await lifecycle.CompleteAsync();

            runtime.LoggerFactory.CreateLogger<MoveToKafkaRetryTopicContinuation>().LogInformation(
                "Routed message {Id} to Kafka retry topic {Topic} (tier {Tier}, attempt {Attempt})",
                envelope.Id, retryTopicName, nextTier, attempt);
        }
        catch (Exception e)
        {
            runtime.LoggerFactory.CreateLogger<MoveToKafkaRetryTopicContinuation>().LogError(e,
                "Failed to route message {Id} to Kafka retry topic {Topic}; moving to dead letter queue",
                envelope.Id, retryTopicName);
            await lifecycle.MoveToDeadLetterQueueAsync(exception);
        }
    }

    private static void StampHeader(Message<string, byte[]> message, string key, string value)
    {
        message.Headers!.Remove(key);
        message.Headers.Add(key, Encoding.UTF8.GetBytes(value));
    }

    private static string? ReadStringHeader(Envelope envelope, string key)
    {
        return envelope.Headers.TryGetValue(key, out var value) ? value : null;
    }

    private static int ReadIntHeader(Envelope envelope, string key, int fallback)
    {
        return envelope.Headers.TryGetValue(key, out var value)
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
