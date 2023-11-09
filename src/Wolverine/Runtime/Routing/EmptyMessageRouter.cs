namespace Wolverine.Runtime.Routing;

public class EmptyMessageRouter<T> : MessageRouterBase<T>
{
    public EmptyMessageRouter(WolverineRuntime runtime) : base(runtime)
    {
    }

    public override IMessageRoute[] Routes => Array.Empty<MessageRoute>();

    public override Envelope[] RouteForSend(T message, DeliveryOptions? options)
    {
        throw new IndeterminateRoutesException(typeof(T));
    }

    public override Envelope[] RouteForPublish(T message, DeliveryOptions? options)
    {
        return Array.Empty<Envelope>();
    }

    public override IMessageRoute FindSingleRouteForSending()
    {
        throw new IndeterminateRoutesException(typeof(T));
    }
}