using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class MoveToErrorQueueSource : IContinuationSource
{
    public string Description => "Move to error queue";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new MoveToErrorQueue(ex);
    }
}

internal class MoveToErrorQueue : IContinuation
{
    private readonly Exception _exception;

    public MoveToErrorQueue(Exception exception)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        await lifecycle.SendFailureAcknowledgementAsync(
            $"Moved message {lifecycle.Envelope!.Id} to the Error Queue.\n{_exception}");

        await lifecycle.MoveToDeadLetterQueueAsync(_exception);

        runtime.MessageLogger.MessageFailed(lifecycle.Envelope, _exception);
        runtime.MessageLogger.MovedToErrorQueue(lifecycle.Envelope, _exception);
    }

    public override string ToString()
    {
        return "Move to Error Queue";
    }

    protected bool Equals(MoveToErrorQueue other)
    {
        return Equals(_exception, other._exception);
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

        return Equals((MoveToErrorQueue)obj);
    }

    public override int GetHashCode()
    {
        return _exception.GetHashCode();
    }
}