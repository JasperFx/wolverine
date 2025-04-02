using System.Diagnostics;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine.ErrorHandling;

internal class MoveToRetryQueueSource : IContinuationSource
{
    public string Description => "Move to retry queue";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new MoveToRetryQueue(ex);
    }
}

internal class MoveToRetryQueue : IContinuation
{
    public MoveToRetryQueue(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception Exception { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        if (lifecycle.Envelope.Listener is ISupportRetryLetterQueue retryListener &&
            !retryListener.RetryLimitReached(lifecycle.Envelope))
        {
            if (lifecycle.Envelope.Message != null)
            {
                lifecycle.Envelope.MessageType = lifecycle.Envelope.Message.GetType().ToMessageTypeName();
            }

            await retryListener.MoveToRetryQueueAsync(lifecycle.Envelope, Exception);
            activity?.AddEvent(new ActivityEvent(WolverineTracing.MovedToRetryQueue));

            runtime.MessageTracking.Requeued(lifecycle.Envelope); // TODO: new method below or this?
            runtime.MessageTracking.MovedToRetryQueue(lifecycle.Envelope, Exception);

        }
        else
        {
            await buildFallbackContinuation(lifecycle)
                .ExecuteAsync(lifecycle, runtime, now, activity);
        }
    }

    private IContinuation buildFallbackContinuation(IEnvelopeLifecycle lifecycle)
    {
        var cs = new MoveToErrorQueueWithoutFailureAckSource();
        var continuation = cs.Build(Exception, lifecycle.Envelope);
        return continuation;
    }

    public override string ToString()
    {
        return "Move to Retry Queue";
    }

    protected bool Equals(MoveToRetryQueue other)
    {
        return Equals(Exception, other.Exception);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((MoveToRetryQueue)obj);
    }

    public override int GetHashCode()
    {
        return Exception.GetHashCode();
    }
}