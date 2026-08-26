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
        // TODO -- at some point, we need a more systematic way of doing this
        // Defensive: a malformed system envelope (no Destination) shouldn't NRE here before
        // EnableAutomaticFailureAcks even gets a chance to short-circuit. The envelope itself is
        // always present (the block below already relies on it); only Destination can be null. GH-3013.
        var scheme = lifecycle.Envelope!.Destination?.Scheme;
        if (scheme is not null && runtime.Options.EnableAutomaticFailureAcks && scheme != TransportConstants.Local && scheme != "external-table")
        {
            await lifecycle.SendFailureAcknowledgementAsync(
                $"Moved message {lifecycle.Envelope!.Id} to the Error Queue.\n{Exception}");
        }

        if (lifecycle.Envelope.Message != null)
        {
            lifecycle.Envelope.MessageType = lifecycle.Envelope.Message.GetType().ToMessageTypeName();
        }
        else
        {
            lifecycle.Envelope.MessageType ??= $"unknown/{Exception.GetType().Name}";
        }

        await lifecycle.MoveToDeadLetterQueueAsync(Exception);

        // Auto-publish Fault<T> if opted in. The publish enrols in the active
        // outbox transaction when one is open on the inbound MessageContext;
        // otherwise it is a best-effort post-DLQ-move publish. Never throws.
        await runtime.PublishFaultIfEnabledAsync(lifecycle, Exception, FaultTrigger.MovedToErrorQueue, activity);

        await lifecycle.CompleteAsync();

        activity?.AddEvent(new ActivityEvent(WolverineTracing.MovedToErrorQueue));

        // GH-4136: MovedToErrorQueue only. It is reported LAST because a terminal record sweeps every
        // earlier record for the envelope complete, and the durable transports' own dead-letter move
        // emits a trailing Sent that nothing else will ever complete. The MessageFailed call that used
        // to sit alongside this is gone: two terminal records on one path cannot be ordered to satisfy
        // both that sweep and the guarantee that the ending record is present. Its dead-letter counter,
        // effective time and failure wire tap now live in MovedToErrorQueue, so no metric is lost.
        lifecycle.CompletionTrackerFor(runtime).MovedToErrorQueue(lifecycle.Envelope, Exception);
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