using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.RemoteInvocation;

internal class AcknowledgementHandler : IMessageHandler
{
    private readonly IReplyTracker _replies;

    public AcknowledgementHandler(IReplyTracker replies)
    {
        _replies = replies;
    }

    public Type MessageType => typeof(Acknowledgement);
    public LogLevel ExecutionLogLevel => LogLevel.Debug;

    public Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        _replies.Complete(context.Envelope!);
        return Task.CompletedTask;
    }
}