using System.Diagnostics;
using JasperFx.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.RateLimiting;

internal sealed class RateLimitContinuationSource : IContinuationSource
{
    public string Description => "Rate limit exceeded; reschedule and pause listener";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        if (ex is RateLimitExceededException rateLimit)
        {
            return new RateLimitContinuation(rateLimit.RetryAfter);
        }

        return new RateLimitContinuation(1.Milliseconds());
    }
}

internal sealed class RateLimitContinuation : IContinuation
{
    public RateLimitContinuation(TimeSpan pauseTime)
    {
        PauseTime = pauseTime <= TimeSpan.Zero ? 1.Milliseconds() : pauseTime;
    }

    public TimeSpan PauseTime { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        await lifecycle.ReScheduleAsync(now.Add(PauseTime)).ConfigureAwait(false);
        await new PauseListenerContinuation(PauseTime)
            .ExecuteAsync(lifecycle, runtime, now, activity).ConfigureAwait(false);
    }
}
