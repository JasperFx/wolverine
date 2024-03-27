using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    internal readonly List<IMessageTransformation> MessageTransformations = new();
    
    internal readonly List<IMessageRouteSource> InternalRouteSources = new()
    {
        new TransformedMessageRouteSource(),
        new AgentMessages(),
        new ExplicitRouting(),
        new LocalRouting(),
        new MessageRoutingConventions()
    };

    internal readonly List<IMessageRouteSource> CustomRouteSources = new();

    internal IEnumerable<IMessageRouteSource> RouteSources()
    {
        foreach (var routeSource in CustomRouteSources)
        {
            yield return routeSource;
        }

        foreach (var routeSource in InternalRouteSources)
        {
            yield return routeSource;
        }
    }


    /// <summary>
    /// Advanced usage of Wolverine to register programmatic message routing rules
    /// </summary>
    /// <param name="messageRouteSource"></param>
    public void PublishWithMessageRoutingSource(IMessageRouteSource messageRouteSource)
    {
        CustomRouteSources.Add(messageRouteSource);
    }
}

internal interface IMessageTransformation
{
    Type SourceType { get; }
    Type DestinationType { get; }
    IMessageRoute CreateRoute(IMessageRoute inner);
}

internal class MessageTransformation<TSource, TDestination> : IMessageTransformation
{
    private readonly Func<TSource, TDestination> _transformation;

    public MessageTransformation(Func<TSource, TDestination> transformation)
    {
        _transformation = transformation;
    }

    public Type SourceType => typeof(TSource);
    public Type DestinationType => typeof(TDestination);
    public IMessageRoute CreateRoute(IMessageRoute inner)
    {
        return new TransformedMessageRoute<TSource, TDestination>(_transformation, inner);
    }
}