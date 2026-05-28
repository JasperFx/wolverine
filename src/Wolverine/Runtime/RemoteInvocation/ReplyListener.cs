using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Runtime.RemoteInvocation;

internal class ReplyListener<T> : IReplyListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource<T> _completion;
    private readonly TimeSpan _timeout;
    private readonly ResultTypeRegistry? _resultTypes;

    public ReplyListener(Envelope envelope, ReplyTracker parent, TimeSpan timeout, CancellationToken cancellationToken,
        ResultTypeRegistry? resultTypes = null)
    {
        RequestId = envelope.Id;
        RequestType = envelope.MessageType;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        cancellationToken.Register(onCancellation);

        _completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellation = new CancellationTokenSource(timeout);

        _cancellation.Token.Register(onCancellation);

        _timeout = timeout;
        _resultTypes = resultTypes;
    }

    public string? RequestType { get; set; }

    public ReplyTracker Parent { get; }

    public TaskStatus Status { get; private set; } = TaskStatus.Running;
    public Task<T> Task => _completion.Task;
    public Guid RequestId { get; }

    public void Complete(Envelope envelope)
    {
        var message = envelope.Message;

        // GH-2221 component R: caller-side unwrap. When the response envelope carries a
        // registered Result type, the caller's awaited T determines what we hand back:
        //   - T == the Result wrapper itself (e.g. await InvokeAsync<Result<OrderPlaced>>())
        //         → return the wrapper as-is, even on failure. No exception.
        //   - T == the inner payload (e.g. await InvokeAsync<OrderPlaced>())
        //         → on success unwrap to T; on failure throw ResultFailureException.
        // The same path serves in-process and remote callers; there's a single materialization
        // point in the codebase.
        if (message is not null && _resultTypes is { HasAny: true })
        {
            var registration = _resultTypes.TryFind(message.GetType());
            if (registration != null)
            {
                if (TryCompleteAsResult(message, registration))
                {
                    Parent.Unregister(this);
                    return;
                }
            }
        }

        if (message is T direct)
        {
            _completion.TrySetResult(direct);
            Status = TaskStatus.RanToCompletion;
        }
        else if (message is FailureAcknowledgement ack)
        {
            _completion.TrySetException(new WolverineRequestReplyException(ack.Message));
            Status = TaskStatus.Faulted;
        }

        Parent.Unregister(this);
    }

    private bool TryCompleteAsResult(object message, IResultTypeRegistration registration)
    {
        var failed = registration.ShouldStop(message);

        // Caller awaited the wrapper directly (e.g. Result<OrderPlaced>). Hand the wrapper back
        // unchanged on both success and failure — they explicitly opted out of the unwrap-or-throw
        // behaviour so they could inspect the failure.
        if (message is T wrapper)
        {
            _completion.TrySetResult(wrapper);
            Status = TaskStatus.RanToCompletion;
            return true;
        }

        // Caller awaited the inner payload. On failure, surface the errors as a typed exception
        // they can catch; on success, unwrap and complete with the inner T.
        if (failed)
        {
            var errors = (registration.Errors(message) ?? Array.Empty<string>()).ToList();
            _completion.TrySetException(new ResultFailureException(errors));
            Status = TaskStatus.Faulted;
            return true;
        }

        var unwrapped = registration.Unwrap(message);
        if (unwrapped is T inner)
        {
            _completion.TrySetResult(inner);
            Status = TaskStatus.RanToCompletion;
            return true;
        }

        // The registration matched the runtime type but the unwrapped payload isn't assignable to
        // T — could happen if the caller asks for the wrong T against an open-generic registration.
        // Fall through to the normal `is T` check so the caller gets a deterministic failure.
        return false;
    }

    public void Dispose()
    {
        _cancellation?.Dispose();
    }

    private void onCancellation()
    {
        // Unregister before setting the exception so the listener is removed
        // before any continuation runs (TaskCreationOptions.RunContinuationsAsynchronously
        // can cause the awaiter to resume before subsequent lines execute)
        Parent.Unregister(this);

        Status = TaskStatus.Faulted;

        if (typeof(T) == typeof(Acknowledgement))
        {
            _completion?.TrySetException(new TimeoutException(
                $"Timed out waiting for expected acknowledgement for original message {RequestId} of type {RequestType ?? "None"} on Node {Parent.AssignedNodeNumber}"));
        }
        else
        {
            _completion?.TrySetException(new TimeoutException(
                $"Timed out waiting for expected response {typeof(T).FullNameInCode()} for original message {RequestId} of type {RequestType} with a configured timeout of {_timeout.TotalMilliseconds} milliseconds"));
        }
    }
}