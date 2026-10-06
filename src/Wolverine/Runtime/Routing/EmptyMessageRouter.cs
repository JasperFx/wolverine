namespace Wolverine.Runtime.Routing;

/// <summary>
///     The router for a message type with no routes at all. Non-generic since GH-4848; see
///     <see cref="MessageRouterBase" />.
/// </summary>
public class EmptyMessageRouter : MessageRouterBase
{
    public EmptyMessageRouter(WolverineRuntime runtime, Type messageType) : base(runtime, messageType)
    {
    }

    public override IMessageRoute[] Routes => Array.Empty<MessageRoute>();

    public override Envelope[] RouteForSend(object message, DeliveryOptions? options)
    {
        throw new IndeterminateRoutesException(MessageType);
    }

    public override Envelope[] RouteForPublish(object message, DeliveryOptions? options)
    {
        return [];
    }

    public override IMessageRoute FindSingleRouteForSending()
    {
        throw new IndeterminateRoutesException(MessageType);
    }
}

/// <summary>
///     Retained for compatibility. The runtime builds the non-generic <see cref="EmptyMessageRouter" />
///     since GH-4848 and never constructs this one; see <see cref="MessageRouterBase" />.
/// </summary>
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
        return [];
    }

    public override IMessageRoute FindSingleRouteForSending()
    {
        throw new IndeterminateRoutesException(typeof(T));
    }
}
