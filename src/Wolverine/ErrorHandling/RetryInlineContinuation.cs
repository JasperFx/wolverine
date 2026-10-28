using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.ErrorHandling;

internal class RetryInlineContinuation : IContinuation, IContinuationSource, IInlineContinuation, IJitterable
{
    public static readonly RetryInlineContinuation Instance = new();

    private readonly TimeSpan? _delay;
    private volatile IJitterStrategy? _jitter;

    private RetryInlineContinuation()
    {
    }

    public RetryInlineContinuation(TimeSpan delay)
    {
        _delay = delay;
    }

    public TimeSpan? Delay => _delay;

    public bool TrySetJitter(IJitterStrategy strategy)
    {
        if (_delay == null) return false;
        _jitter = strategy;
        return true;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        if (_delay != null)
        {
            var effective = _jitter?.Apply(_delay.Value, lifecycle.Envelope?.Attempts ?? 1) ?? _delay.Value;
            if (!await waitOutCooldownAsync(effective, lifecycle, runtime).ConfigureAwait(false))
            {
                runtime.Logger?.LogInformation(
                    "Abandoned the retry cooldown for envelope {EnvelopeId} ({MessageType}) because its listener is stopping. The message is left unsettled, for the broker to redeliver or for inbox recovery",
                    lifecycle.Envelope?.Id, lifecycle.Envelope?.MessageType);

                return;
            }
        }

        activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeRetry));
        WolverineTracing.RecordPreviousAttempt(lifecycle.Envelope, activity);

        await lifecycle.RetryExecutionNowAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// GH-5257. Wait out the cooldown unless the runtime or this message's listener starts stopping first.
    /// False means the wait was interrupted, and then the retry must not run: the stop that interrupted it
    /// is about to join this very execution, and a handler run after the stop signal is exactly what the
    /// stop is trying to prevent. The message is deliberately NOT settled. A broker delivery stays
    /// unacknowledged and is redelivered, and a durable inbox row stays owned until shutdown releases it
    /// for recovery -- the same as any other message still in flight when a listener stops.
    /// </summary>
    private static async Task<bool> waitOutCooldownAsync(TimeSpan delay, IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime)
    {
        var runtimeStopping = runtime is WolverineRuntime r ? r.Stopping : CancellationToken.None;
        var listenerStopping = lifecycle.Envelope?.Listener?.Pipeline is HandlerPipeline pipeline
            ? pipeline.Stopping
            : CancellationToken.None;

        if (!runtimeStopping.CanBeCanceled && !listenerStopping.CanBeCanceled)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            return true;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(runtimeStopping, listenerStopping);

        try
        {
            await Task.Delay(delay, stopping.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public string Description =>
        _delay == null ? "Retry inline with no delay" : "Retry inline with a delay of " + _delay;

    IContinuation IContinuationSource.Build(Exception ex, Envelope envelope)
    {
        return this;
    }

    public override string ToString()
    {
        return "Retry Now";
    }

    public async ValueTask<InvokeResult> ExecuteInlineAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now,
        Activity? activity, CancellationToken cancellation)
    {
        if (Delay.HasValue)
        {
            var effective = _jitter?.Apply(Delay.Value, lifecycle.Envelope?.Attempts ?? 1) ?? Delay.Value;
            await Task.Delay(effective, cancellation).ConfigureAwait(false);
        }

        return InvokeResult.TryAgain;
    }
}
