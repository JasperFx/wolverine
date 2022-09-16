using System;
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

public class MoveToErrorQueue : IContinuation
{
    private readonly Exception _exception;

    public MoveToErrorQueue(Exception exception)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public async ValueTask ExecuteAsync(IMessageContext context,
        IWolverineRuntime runtime,
        DateTimeOffset now)
    {
        await context.SendFailureAcknowledgementAsync($"Moved message {context.Envelope!.Id} to the Error Queue.\n{_exception}");

        await context.MoveToDeadLetterQueueAsync(_exception);

        runtime.MessageLogger.MessageFailed(context.Envelope, _exception);
        runtime.MessageLogger.MovedToErrorQueue(context.Envelope, _exception);
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
