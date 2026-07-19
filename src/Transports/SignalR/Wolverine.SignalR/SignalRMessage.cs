using Wolverine.Runtime;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

/// <summary>
/// Message that is explicitly routed to a specific subset of SignalR
/// connections
/// </summary>
/// <param name="Message"></param>
/// <param name="Locator"></param>
/// <typeparam name="T"></typeparam>
public record SignalRMessage<T>(T Message, WebSocketRouting.ILocator Locator) : ISendMyself
{
    async ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        var options = new DeliveryOptions { SagaId = Locator.ToString(), RoutingInformation = Locator };

        // GH-3499 -- if the handler's context has already flushed its outgoing
        // messages (e.g. an explicit SaveChangesAsync() on an outboxed session
        // drained it), enqueueing here would be silently dropped by the
        // MultiFlushMode.OnlyOnce guard. Send through a fresh context instead
        if (context is MessageContext { HasFlushed: true } flushed)
        {
            var fresh = new MessageContext(flushed.Runtime)
            {
                TenantId = flushed.TenantId,
                CorrelationId = flushed.CorrelationId
            };

            await fresh.PublishAsync(Message, options);
            await fresh.FlushOutgoingMessagesAsync();
            return;
        }

        await context.PublishAsync(Message, options);
    }
}
