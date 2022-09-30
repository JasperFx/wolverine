using System;
using System.Threading;
using System.Threading.Tasks;
using LamarCodeGeneration;

namespace Wolverine.Runtime.ResponseReply;

internal class ReplyListener<T> : IReplyListener where T : class
{
    private readonly TaskCompletionSource<T> _completion;
    private readonly CancellationTokenSource _cancellation;
    public Guid RequestId { get; }
    public ResponseHandler Parent { get; }

    public TaskStatus Status { get; private set; } = TaskStatus.Running;
    public Task<T> Task => _completion.Task;

    public ReplyListener(Guid requestId, ResponseHandler parent, TimeSpan timeout)
    {
        RequestId = requestId;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));

        _completion = new TaskCompletionSource<T>();
        _cancellation = new CancellationTokenSource(timeout);

        _cancellation.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                $"Timed out waiting for expected response {typeof(T).FullNameInCode()} for original message {RequestId}"));

            Parent.Unregister(this);

            Status = TaskStatus.Faulted;
        });
    }

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
}