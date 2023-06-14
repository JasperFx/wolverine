using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.RemoteInvocation;

internal class FailureAcknowledgementHandler : IMessageHandler
{
    private readonly IReplyTracker _replies;

    public FailureAcknowledgementHandler(IReplyTracker replies)
    {
        _replies = replies;
    }

    public Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        _replies.Complete(context.Envelope!);
        return Task.CompletedTask;
    }

    public Type MessageType => typeof(FailureAcknowledgement);
    public LogLevel ExecutionLogLevel => LogLevel.None;
}