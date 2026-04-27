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

        // The wrapper that just fired carries the context-correlation fields
        // that were stamped on the way out (TenantId, CorrelationId, UserName,
        // ParentId, ConversationId). Copy them onto the inner before forwarding
        // so the eventual handler runs under the original tenant / correlation.
        // See GH-2571 / PR #2572.
        scheduled.CopyContextCorrelationFrom(context.Envelope);

        return context.ForwardScheduledEnvelopeAsync(scheduled).AsTask();
    }
}