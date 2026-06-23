using System.Diagnostics;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.Pulsar.ErrorHandling;

/// <summary>
/// Discoverable error-policy continuation for Pulsar's native tiered retry-letter topics (GH-3182), the
/// Pulsar analogue of <c>MoveToKafkaRetryTopic</c>. On failure it routes the message through the
/// retry-letter topic with the configured per-tier delays (each tier reprocessed by the retry consumer
/// after its delay elapses), and after the last tier is exhausted it moves the message to the dead-letter
/// topic.
///
/// The configured delays are wired onto the matching Pulsar listener endpoints at startup
/// (<see cref="PulsarTransport"/>), which spins up the retry-letter producer + consumer and the DLQ; the
/// runtime routing itself is performed by Pulsar's native resiliency pipeline. This continuation only ever
/// acts on Pulsar listeners — a failure arriving over any other transport falls back to a normal inline
/// retry, so the Pulsar retry-letter routing can never be applied cross-transport.
/// </summary>
internal sealed class MoveToPulsarRetryTopicContinuation : UserDefinedContinuation
{
    private readonly TimeSpan[] _delays;
    private readonly Exception? _exception;

    public MoveToPulsarRetryTopicContinuation(TimeSpan[] delays) : this(delays, null)
    {
    }

    private MoveToPulsarRetryTopicContinuation(TimeSpan[] delays, Exception? exception)
        : base($"Move to Pulsar retry topic ({delays.Length} tiers)")
    {
        _delays = delays;
        _exception = exception;
    }

    public TimeSpan[] Delays => _delays;

    public override IContinuation Build(Exception ex, Envelope envelope)
    {
        if (envelope.Listener is PulsarListener)
        {
            return new MoveToPulsarRetryTopicContinuation(_delays, ex);
        }

        // Anything arriving over another transport degrades to a normal inline retry — never
        // cross-transport.
        return RetryInlineContinuation.Instance;
    }

    public override async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        var envelope = lifecycle.Envelope!;
        var exception = _exception ?? envelope.Failure ?? new Exception("Unknown failure");

        if (envelope.Listener is not PulsarListener listener)
        {
            await RetryInlineContinuation.Instance.ExecuteAsync(lifecycle, runtime, now, activity);
            return;
        }

        // Tiers remaining -> schedule into the retry-letter topic at this attempt's delay. The native
        // retry consumer reprocesses the message once the delay elapses, incrementing the attempt count.
        if (listener.NativeRetryLetterQueueEnabled && listener.RetryLetterTopic is { } retry &&
            retry.Retry.Count >= envelope.Attempts)
        {
            await new ScheduledRetryContinuation(retry.Retry[envelope.Attempts - 1])
                .ExecuteAsync(lifecycle, runtime, now, activity);
            return;
        }

        // Tiers exhausted -> native dead-letter topic.
        if (listener.NativeDeadLetterQueueEnabled)
        {
            await new MoveToErrorQueue(exception).ExecuteAsync(lifecycle, runtime, now, activity);
            return;
        }

        // No native retry-letter or dead-letter topic is in effect (e.g. an unsupported subscription
        // type); degrade to an inline retry rather than silently dropping the failure.
        await RetryInlineContinuation.Instance.ExecuteAsync(lifecycle, runtime, now, activity);
    }
}
