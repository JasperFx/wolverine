using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.RemoteInvocation;

internal class ReplyListener<T> : IReplyListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<T> _completion;
    private readonly TimeSpan _timeout;

    public ReplyListener(Envelope envelope, ReplyTracker parent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        RequestId = envelope.Id;
        RequestType = envelope.MessageType;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        cancellationToken.Register(onCancellation);

        _completion = new TaskCompletionSource<T>();
        _cancellation = new CancellationTokenSource(timeout);

        _cancellation.Token.Register(onCancellation);

        _timeout = timeout;
    }

    public string? RequestType { get; set; }

    public ReplyTracker Parent { get; }

    public TaskStatus Status { get; private set; } = TaskStatus.Running;
    public Task<T> Task => _completion.Task;
    public Guid RequestId { get; }

    public void Complete(Envelope envelope)
    {
        if (envelope.Message is T message)
        {
            _completion.TrySetResult(message);
            Status = TaskStatus.RanToCompletion;
        }
        else if (envelope.Message is FailureAcknowledgement ack)
        {
            _completion.TrySetException(new WolverineRequestReplyException(ack.Message));
            Status = TaskStatus.Faulted;
        }

        Parent.Unregister(this);
    }

    public void Dispose()
    {
        _cancellation?.Dispose();
    }

    private void onCancellation()
    {
        if (typeof(T) == typeof(Acknowledgement))
        {
            _completion?.TrySetException(new TimeoutException(
                $"Timed out waiting for expected acknowledgement for original message {RequestId} of type {RequestType ?? "None"}"));
        }
        else
        {
            _completion?.TrySetException(new TimeoutException(
                $"Timed out waiting for expected response {typeof(T).FullNameInCode()} for original message {RequestId} of type {RequestType} with a configured timeout of {_timeout.TotalMilliseconds} milliseconds"));
        }

        Parent.Unregister(this);

        Status = TaskStatus.Faulted;
    }
}