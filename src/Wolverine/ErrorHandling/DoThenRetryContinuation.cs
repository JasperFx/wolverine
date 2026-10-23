using System.Diagnostics;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.ErrorHandling;

/// <summary>
/// GH-5068. Runs a user supplied recovery action, then retries the message immediately. Works both for
/// messages received from a queue, where the retry is <see cref="IEnvelopeLifecycle.RetryExecutionNowAsync" />,
/// and for inline execution through <c>IMessageBus.InvokeAsync()</c>,
/// where the retry is <see cref="InvokeResult.TryAgain" />
/// </summary>
internal class DoThenRetryContinuationSource : IContinuationSource
{
    private readonly Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> _action;

    public DoThenRetryContinuationSource(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        string description)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        Description = description;
    }

    public string Description { get; }

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new DoThenRetryContinuation(_action, ex, Description);
    }
}

internal class DoThenRetryContinuation : IContinuation, IInlineContinuation
{
    private readonly Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> _action;
    private readonly string _description;

    public DoThenRetryContinuation(Func<IWolverineRuntime, IEnvelopeLifecycle, Exception, ValueTask> action,
        Exception exception, string description)
    {
        _action = action;
        Exception = exception;
        _description = description;
    }

    public Exception Exception { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        await _action(runtime, lifecycle, Exception).ConfigureAwait(false);

        activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeRetry));
        WolverineTracing.RecordPreviousAttempt(lifecycle.Envelope, activity);

        await lifecycle.RetryExecutionNowAsync().ConfigureAwait(false);
    }

    public async ValueTask<InvokeResult> ExecuteInlineAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity, CancellationToken cancellation)
    {
        await _action(runtime, lifecycle, Exception).ConfigureAwait(false);

        activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeRetry));

        return InvokeResult.TryAgain;
    }

    public override string ToString()
    {
        return _description;
    }
}
