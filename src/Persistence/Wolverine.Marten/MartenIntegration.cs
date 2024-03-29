using JasperFx.Core.Reflection;
using Marten.Events;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Marten;

internal class MartenIntegration : IWolverineExtension, IEventForwarding
{
    private readonly List<Action<WolverineOptions>> _actions = new();
    
    /// <summary>
    ///     This directs the Marten integration to try to publish events out of the enrolled outbox
    ///     for a Marten session on SaveChangesAsync()
    /// </summary>
    public bool ShouldPublishEvents { get; set; }

    public void Configure(WolverineOptions options)
    {
        options.CodeGeneration.Sources.Add(new MartenBackedPersistenceMarker());

        options.CodeGeneration.AddPersistenceStrategy<MartenPersistenceFrameProvider>();

        options.CodeGeneration.Sources.Add(new SessionVariableSource());

        options.Policies.Add<MartenAggregateHandlerStrategy>();

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);
        
        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());
    }

    internal MartenEventRouter EventRouter { get; } = new();

    EventForwardingTransform<T> IEventForwarding.SubscribeToEvent<T>()
    {
        return new EventForwardingTransform<T>(EventRouter);
    }
}

internal class EventWrapperForwarder : IHandledTypeRule
{
    public bool TryFindHandledType(Type concreteType, out Type handlerType)
    {
        handlerType = concreteType.FindInterfaceThatCloses(typeof(IEvent<>));
        return handlerType != null;
    }
}

internal class MartenEventRouter : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.Closes(typeof(IEvent<>)))
        {
            var eventType = messageType.GetGenericArguments().Single();
            var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);

            if (messageType.IsConcrete())
            {
                return runtime.RoutingFor(wrappedType).Routes;
            }
            
            MessageRoute[] innerRoutes = Array.Empty<MessageRoute>();
            if (messageType.IsConcrete())
            {
                var inner = runtime.RoutingFor(wrappedType);
                innerRoutes = inner.Routes.OfType<MessageRoute>().ToArray();
            }
            
            // First look for explicit transformations
            var transformers = Transformers.Where(x => x.SourceType == wrappedType);
            var transformed = transformers.SelectMany(x =>
                runtime.RoutingFor(x.DestinationType).Routes.Select(x.CreateRoute));

            var forEventType =  runtime.RoutingFor(eventType).Routes.Select(route =>
                typeof(EventUnwrappingMessageRoute<>).CloseAndBuildAs<IMessageRoute>(route, eventType));

            var candidates = forEventType.Concat(transformed).Concat(innerRoutes).ToArray();
            return candidates;
        }
        else
        {
            return Array.Empty<IMessageRoute>();
        }
    }

    public bool IsAdditive => false;
    public List<IMessageTransformation> Transformers { get; } = new();
}

internal class EventUnwrappingMessageRoute<T> : TransformedMessageRoute<IEvent<T>, T>
{
    public EventUnwrappingMessageRoute(IMessageRoute inner) : base(e => e.Data, inner)
    {
    }
}

public interface IEventForwarding   
{
    /// <summary>
    /// Subscribe to an event, but with a transformation. The transformed message will be
    /// published to Wolverine with its normal routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    EventForwardingTransform<T> SubscribeToEvent<T>();
}

public class EventForwardingTransform<TSource>
{
    private readonly MartenEventRouter _martenEventWrapper;

    internal EventForwardingTransform(MartenEventRouter martenEventWrapper)
    {
        _martenEventWrapper = martenEventWrapper;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        var transformation = new MessageTransformation<IEvent<TSource>, TDestination>(transformer);
        _martenEventWrapper.Transformers.Add(transformation);
    }
}