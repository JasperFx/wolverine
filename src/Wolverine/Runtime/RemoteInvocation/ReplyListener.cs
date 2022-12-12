using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;

namespace Wolverine.Runtime.RemoteInvocation;

internal class ReplyListener<T> : IReplyListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<T> _completion;

    public ReplyListener(Guid requestId, ReplyTracker parent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        RequestId = requestId;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        cancellationToken.Register(onCancellation);

        _completion = new TaskCompletionSource<T>();
        _cancellation = new CancellationTokenSource(timeout);

        _cancellation.Token.Register(onCancellation);
    }

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
        _cancellation.Dispose();
    }

    private void onCancellation()
    {
        if (typeof(T) == typeof(Acknowledgement))
        {
            _completion.TrySetException(new TimeoutException(
                $"Timed out waiting for expected acknowledgement for original message {RequestId}"));
        }
        else
        {
            _completion.TrySetException(new TimeoutException(
                $"Timed out waiting for expected response {typeof(T).FullNameInCode()} for original message {RequestId}"));
        }


        Parent.Unregister(this);

        Status = TaskStatus.Faulted;
    }
}