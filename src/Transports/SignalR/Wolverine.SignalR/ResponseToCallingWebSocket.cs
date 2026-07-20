using Wolverine.Runtime;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

/// <summary>
/// Wrapped message that publishes the inner Message to
/// the originating SignalR connection
/// </summary>
/// <param name="Message"></param>
/// <typeparam name="T"></typeparam>
public record ResponseToCallingWebSocket<T>(T Message) : ISendMyself
{
    async ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        if (context.Envelope is SignalREnvelope se)
        {
            var options = new DeliveryOptions
                { RoutingInformation = new WebSocketRouting.Connection(se.ConnectionId) };

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

                await fresh.EndpointFor(se.Destination!).SendAsync(Message, options);
                await fresh.FlushOutgoingMessagesAsync();
                return;
            }

            // This gets us back to the same SignalR Hub
            await context.EndpointFor(se.Destination!).SendAsync(Message, options);
            return;
        }

        throw new InvalidWolverineSignalROperationException("The current message was not received from SignalR");
    }
}
