using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.RemoteInvocation;

internal class AcknowledgementHandler : MessageHandler
{
    private readonly IReplyTracker _replies;

    public AcknowledgementHandler(IReplyTracker replies)
    {
        _replies = replies;
    }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        _replies.Complete(context.Envelope!);
        return Task.CompletedTask;
    }
}