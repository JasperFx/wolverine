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
    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        if (context.Envelope is SignalREnvelope se)
        {
            // This gets us back to the same SignalR Hub
            return context
                .EndpointFor(se.Destination)
                .SendAsync(Message, new DeliveryOptions { RoutingInformation = new WebSocketRouting.Connection(se.ConnectionId) });
        }

        throw new InvalidWolverineSignalROperationException("The current message was not received from SignalR");
    }
}