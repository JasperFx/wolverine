using System.Diagnostics;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.ErrorHandling;

internal class RetryInlineContinuation : IContinuation, IContinuationSource, IInlineContinuation
{
    public static readonly RetryInlineContinuation Instance = new();

    private readonly TimeSpan? _delay;

    private RetryInlineContinuation()
    {
    }

    public RetryInlineContinuation(TimeSpan delay)
    {
        _delay = delay;
    }

    public TimeSpan? Delay => _delay;

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        if (_delay != null)
        {
            await Task.Delay(_delay.Value).ConfigureAwait(false);
        }

        activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeRetry));

        await lifecycle.RetryExecutionNowAsync().ConfigureAwait(false);
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
            await Task.Delay(Delay.Value, cancellation).ConfigureAwait(false);
        }

        return InvokeResult.TryAgain;
    }
}