using JasperFx.Core.Reflection;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

public interface IMessageRoute
{
    Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime);

}

internal class TransformedMessageRouteSource : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        var transformations = runtime.Options.MessageTransformations.Where(x => x.SourceType == messageType);
        return transformations.SelectMany(t => runtime.RoutingFor(t.DestinationType).Routes.Select(t.CreateRoute)).ToArray();

    }

    public bool IsAdditive => true;
}

internal class TransformedMessageRoute<TSource, TDestination> : IMessageRoute
{
    private readonly Func<TSource, TDestination> _transformation;
    private readonly IMessageRoute _inner;

    public TransformedMessageRoute(Func<TSource, TDestination> transformation, IMessageRoute inner)
    {
        _transformation = transformation;
        _inner = inner;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime)
    {
        var transformed = _transformation((TSource)message);
        return _inner.CreateForSending(transformed!, options, localDurableQueue, runtime);
    }
}