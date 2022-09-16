using System;

namespace Wolverine.Runtime.Routing;

internal class EmptyMessageRouter<T> : MessageRouterBase<T>
{
    public EmptyMessageRouter(WolverineRuntime runtime) : base(runtime)
    {
    }

    public override Envelope[] RouteForSend(T message, DeliveryOptions? options)
    {
        throw new NoRoutesException(typeof(T));
    }

    public override Envelope[] RouteForPublish(T message, DeliveryOptions? options)
    {
        return Array.Empty<Envelope>();
    }
}
