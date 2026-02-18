using Microsoft.Extensions.Logging;
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

        context.Runtime.Logger.LogDebug("Forwarding previously scheduled envelope {EnvelopeId} ({MessageType}) for execution to {Destination}", scheduled.Id, scheduled.MessageType, scheduled.Destination);

        scheduled.Source = context.Runtime.Options.ServiceName;
        scheduled.ScheduledTime = null;
        scheduled.Status = EnvelopeStatus.Outgoing;

        return context.ForwardScheduledEnvelopeAsync(scheduled).AsTask();
    }
}