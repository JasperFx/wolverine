using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

public interface IMessageRoute
{
    Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName);
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
        WolverineRuntime runtime, string? topicName)
    {
        var transformed = _transformation((TSource)message);
        return _inner.CreateForSending(transformed!, options, localDurableQueue, runtime, topicName);
    }
}

public class TopicRouting<T> : IMessageRouteSource, IMessageRoute
{
    private readonly Func<T, string> _topicSource;
    private readonly Endpoint _topicEndpoint;
    private IMessageRoute? _route;

    public TopicRouting(Func<T, string> topicSource, Endpoint topicEndpoint)
    {
        _topicSource = topicSource;
        _topicEndpoint = topicEndpoint;
    }

    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.CanBeCastTo<T>())
        {
            yield return this;
        }
    }

    public bool IsAdditive => true;

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        if (message is T typedMessage)
        {
            _route ??= _topicEndpoint.RouteFor(typeof(T), runtime);
            topicName ??= _topicSource(typedMessage);
            var envelope = _route.CreateForSending(message, options, localDurableQueue, runtime, topicName);

            // This is an unfortunate timing of operation issue.
            if (envelope is { Message: Envelope scheduled, Status: EnvelopeStatus.Scheduled })
            {
                scheduled.TopicName = envelope.TopicName;
            }

            return envelope;
        }

        throw new InvalidOperationException(
            $"The message of type {message.GetType().FullNameInCode()} cannot be routed as a message of type {typeof(T).FullNameInCode()}");
    }
}
