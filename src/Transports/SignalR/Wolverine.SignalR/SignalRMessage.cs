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
    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return context.PublishAsync(Message,
            new DeliveryOptions() { SagaId = Locator.ToString(), RoutingInformation = Locator });
    }
}