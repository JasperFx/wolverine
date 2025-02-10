using JasperFx.Core.Reflection;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

#region sample_IMessageRoute

/// <summary>
/// Contains all the rules for where and how an outgoing message
/// should be sent to a single subscriber
/// </summary>
public interface IMessageRoute
{
    Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName);

    SubscriptionDescriptor Describe();
}

#endregion

/// <summary>
/// Diagnostic view of a subscription
/// </summary>
public class SubscriptionDescriptor
{
    public Uri Endpoint { get; init; }
    public string ContentType { get; set; } = "application/json";
    public string Description { get; set; } = string.Empty;

    // TODO -- add something about envelope rules?
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

    public SubscriptionDescriptor Describe()
    {
        var descriptor = _inner.Describe();
        descriptor.Description = "Transformed to " + typeof(TDestination).FullNameInCode();
        return descriptor;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var transformed = _transformation((TSource)message);
        return _inner.CreateForSending(transformed!, options, localDurableQueue, runtime, topicName);
    }

    public override string ToString()
    {
        return "Forward message as " + typeof(TDestination).FullNameInCode();
    }
}
