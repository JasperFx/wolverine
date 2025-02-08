using JasperFx;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Internal;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weasel.Core;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.Marten;

public class MartenIntegration : IWolverineExtension, IEventForwarding
{
    private readonly List<Action<WolverineOptions>> _actions = [];

    /// <summary>
    ///     This directs the Marten integration to try to publish events out of the enrolled outbox
    ///     for a Marten session on SaveChangesAsync(). This is the "event forwarding" option.
    /// There is no ordering guarantee with this option, but this will distribute event messages
    /// faster than strictly ordered event subscriptions. Default is false
    /// </summary>
    public bool UseFastEventForwarding { get; set; }
    
    /// <summary>
    /// Use Wolverine to evenly distribute event projection and subscription work of Marten
    /// asynchronous projections. This should be used exclusively with Marten's AddAsyncDaemon() option
    /// and takes the place of Marten's built in, naive load distribution
    /// </summary>
    public bool UseWolverineManagedEventSubscriptionDistribution { get; set; }

    public void Configure(WolverineOptions options)
    {
        options.CodeGeneration.Sources.Add(new MartenBackedPersistenceMarker());

        options.CodeGeneration.InsertFirstPersistenceStrategy<MartenPersistenceFrameProvider>();

        options.CodeGeneration.Sources.Add(new SessionVariableSource());

        options.Policies.Add<MartenAggregateHandlerStrategy>();

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);

        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());

        var transport = options.Transports.GetOrCreate<PostgresqlTransport>();
        transport.TransportSchemaName = TransportSchemaName;
        transport.MessageStorageSchemaName = MessageStorageSchemaName ?? "public";
        
        options.Policies.Add<MartenOpPolicy>();
    }
    
    /// <summary>
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </summary>
    public string? MasterDatabaseConnectionString { get; set; }
    
    /// <summary>
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </summary>
    public NpgsqlDataSource? MasterDataSource { get; set; }

    internal MartenEventRouter EventRouter { get; } = new();

    private string _transportSchemaName = "wolverine_queues";

    /// <summary>
    /// The database schema to place postgres-backed queues. The default is "wolverine_queues"
    /// </summary>
    public string TransportSchemaName
    {
        get => _transportSchemaName;
        set => _transportSchemaName = value.ToLowerInvariant();
    }

    private string? _messageStorageSchemaName;

    /// <summary>
    /// The database schema to place the message store tables for Wolverine
    /// The default is to use the same schema as the Marten DocumentStore
    /// </summary>
    public string? MessageStorageSchemaName
    {
        get => _messageStorageSchemaName;
        set => _messageStorageSchemaName = value?.ToLowerInvariant();
    }
    
    /// <summary>
    /// Optionally override whether to automatically create message database schema objects. Defaults to <see cref="StoreOptions.AutoCreateSchemaObjects"/>
    /// </summary>
    public AutoCreate? AutoCreate { get; set; }

    public EventForwardingTransform<T> SubscribeToEvent<T>()
    {
        return new EventForwardingTransform<T>(EventRouter);
    }
}

internal class MartenOverrides : IConfigureMarten
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        options.Events.MessageOutbox = new MartenToWolverineOutbox(services);
        
        options.Policies.ForAllDocuments(mapping =>
        {
            if (mapping.DocumentType.CanBeCastTo<Saga>())
            {
                mapping.UseNumericRevisions = true;
                mapping.Metadata.Revision.Member = mapping.DocumentType.GetProperty(nameof(Saga.Version));
            }
        });
    }
}

internal class MartenOverrides<T> : MartenOverrides, IConfigureMarten<T> where T : IDocumentStore{}

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

            MessageRoute[] innerRoutes = [];
            if (messageType.IsConcrete())
            {
                var inner = runtime.RoutingFor(wrappedType);
                innerRoutes = inner.Routes.Concat(new LocalRouting().FindRoutes(wrappedType, runtime)).OfType<MessageRoute>().ToArray();
            }
            else
            {
                innerRoutes = new ExplicitRouting().FindRoutes(wrappedType, runtime).OfType<MessageRoute>().ToArray();
                if (!innerRoutes.Any())
                {
                    innerRoutes = new LocalRouting().FindRoutes(wrappedType, runtime).OfType<MessageRoute>().ToArray();
                }
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
    public List<IMessageTransformation> Transformers { get; } = [];
}

internal class EventUnwrappingMessageRoute<T> : TransformedMessageRoute<IEvent<T>, T>
{
    public EventUnwrappingMessageRoute(IMessageRoute inner) : base(e => e.Data, inner)
    {
    }

    public override string ToString()
    {
        return $"Unwrap event wrapper to " + typeof(T).FullNameInCode();
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