using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Scheduled;

internal class ScheduledSendEnvelopeHandler : MessageHandler
{
    public ScheduledSendEnvelopeHandler(HandlerGraph parent)
    {
        Chain = new HandlerChain(typeof(Envelope), parent);
    }

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var scheduled = (Envelope)context.Envelope!.Message!;

        return context.As<MessageContext>().ForwardScheduledEnvelopeAsync(scheduled).AsTask();
    }
}