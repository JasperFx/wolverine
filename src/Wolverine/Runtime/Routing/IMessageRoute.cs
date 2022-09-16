using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

internal interface IMessageRoute
{
    Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime);
}
