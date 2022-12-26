﻿using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class RetryInlineContinuation : IContinuation, IContinuationSource
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

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now)
    {
        if (_delay != null)
        {
            await Task.Delay(_delay.Value).ConfigureAwait(false);
        }

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
}