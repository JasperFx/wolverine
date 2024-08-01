using System.Diagnostics;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;

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
    public MoveToErrorQueue(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception Exception { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        if (lifecycle.Envelope.Destination.Scheme != TransportConstants.Local)
        {
            await lifecycle.SendFailureAcknowledgementAsync(
                $"Moved message {lifecycle.Envelope!.Id} to the Error Queue.\n{Exception}");
        }

        if (lifecycle.Envelope.Message != null)
        {
            lifecycle.Envelope.MessageType = lifecycle.Envelope.Message.GetType().ToMessageTypeName();
        }

        await lifecycle.MoveToDeadLetterQueueAsync(Exception);

        activity?.AddEvent(new ActivityEvent(WolverineTracing.MovedToErrorQueue));

        runtime.MessageTracking.MessageFailed(lifecycle.Envelope, Exception);
        runtime.MessageTracking.MovedToErrorQueue(lifecycle.Envelope, Exception);
    }

    public override string ToString()
    {
        return "Move to Error Queue";
    }

    protected bool Equals(MoveToErrorQueue other)
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

        return Equals((MoveToErrorQueue)obj);
    }

    public override int GetHashCode()
    {
        return Exception.GetHashCode();
    }
}