using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Exceptions;
using Marten.Internal;
using Marten.Schema;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Diagnostics.CodeAnalysis;
using Weasel.Core;
using Wolverine.ErrorHandling;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Marten.Publishing;
using Wolverine.Marten.Requirements;
using Wolverine.Middleware;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS;
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
    /// Use this when using Wolverine to evenly distribute event projection and subscription 
    /// work of Marten asynchronous projections. This replaces Marten's <c>AddAsyncDaemon(HotCold)</c> 
    /// option and should not be used in combination with Marten's own load distribution.
    /// </summary>
    public bool UseWolverineManagedEventSubscriptionDistribution { get; set; }

    public void Configure(WolverineOptions options)
    {
        // Duplicate incoming messages
        options.OnException<MartenCommandException>(e =>
            {
                if (e.InnerException is PostgresException pg)
                {
                    return pg.TableName == DatabaseConstants.IncomingTable && pg.ConstraintName.IsNotEmpty() &&
                           pg.ConstraintName.StartsWith("pkey");
                }

                return false;
            })
            .Discard();
        
        options.CodeGeneration.Sources.Add(new MartenBackedPersistenceMarker());

        // GH-3001: prime the service-location child scope with the handler's outbox-enrolled
        // IDocumentSession so a service-located IDocumentSession / IQuerySession resolves to that same
        // session. The frame self-guards (no-op when the chain has no Marten session).
        options.ScopingFrameSources.Add(() => new PrimeScopedDocumentSessionFrame());

        options.CodeGeneration.InsertFirstPersistenceStrategy<MartenPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new SessionVariableSource());
        options.CodeGeneration.Sources.Add(new DocumentOperationsSource());
        options.CodeGeneration.Sources.Add(new EventStoreOperationsSource());

        options.Policies.Add<MartenAggregateHandlerStrategy>();

        // GH-2944: pre-populate chain.AncillaryStoreType so the message-type-to-ancillary-store
        // map built later in WolverineRuntime.HostService sees it. See MartenStoreEagerPolicy for
        // the Phase A vs Phase B ordering trap this addresses.
        options.Policies.Add<MartenStoreEagerPolicy>();
        
        // QuerySpecificationPolicy detects ICompiledQuery/IQueryPlan-typed variables
        // produced by Load/LoadAsync methods and injects FetchSpecificationFrames to
        // execute them. Must run BEFORE MartenBatchingPolicy so those injected frames
        // (which are IBatchableFrame) are grouped into a single batched query.
        options.CodeGeneration.MethodPreCompilation.Add(new QuerySpecificationPolicy());
        options.CodeGeneration.MethodPreCompilation.Add(new MartenBatchingPolicy());

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

        options.CodeGeneration.AddContinuationStrategy<MartenDataRequirementContinuationStrategy>();
    }

    /// <summary>
    ///     In the case of Marten using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    ///     Wolverine will try to use the master database from the Marten configuration when possible
    /// </summary>
    public string? MainDatabaseConnectionString { get; set; }
    
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

    public EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull
    {
        return new EventForwardingTransform<T>(EventRouter);
    }
}

internal class MartenOverrides : IConfigureMarten
{
    // Null for the main store (uses the runtime's default message store). Ancillary stores
    // override this with their marker type so the outbox targets the ancillary store's own
    // message store (its configured SchemaName / database). See GH-2887.
    protected virtual Type? StoreType => null;

    public void Configure(IServiceProvider services, StoreOptions options)
    {
        options.Events.MessageOutbox = new MartenToWolverineOutbox(services, StoreType);

        // Envelope is Wolverine's operational outbox document. Keep it
        // single-tenant and unpartitioned regardless of blanket document
        // policies the user has applied (AllDocumentsAreMultiTenanted or
        // AllDocumentsAreMultiTenantedWithPartitioning). Without this,
        // two stores that share a database schema can disagree about
        // mt_doc_envelope's shape, producing an impossible
        // "drop partitioning column" migration on the next deploy.
        //
        // These per-type alterations on the DocumentMappingBuilder run
        // AFTER Marten's applyPolicies / applyPostPolicies passes during
        // DocumentMapping construction, so they reliably win over any
        // blanket policy the user registered. See GH-2566 / marten#4268.
        options.Schema.For<Envelope>()
            .SingleTenanted()
            .DoNotPartition();

        options.Policies.ForAllDocuments(mapping =>
        {
            if (mapping.DocumentType.CanBeCastTo<Saga>())
            {
                mapping.UseNumericRevisions = true;
                // GetProperty(name, returnType) — not GetProperty(name) — because
                // saga subclasses in the wild sometimes declare a shadowing
                // `public new ... Version` property. Saga.Version is permanently
                // an int (it aligns with JasperFx 2.0 rc's IRevisioned.Version,
                // which is an int; the long-versioned event-sourcing case is a
                // separate ILongVersioned.Version). Filtering by the int return
                // type picks the canonical Saga.Version revision property and
                // ignores any derived shadow of a different type.
                mapping.Metadata.Revision.Member = mapping.DocumentType.GetProperty(
                    nameof(Saga.Version), typeof(int))!;
            }
        });
    }
}

internal class MartenOverrides<T> : MartenOverrides, IConfigureMarten<T> where T : IDocumentStore
{
    protected override Type? StoreType => typeof(T);
}

internal class EventWrapperForwarder : IHandledTypeRule
{
    public bool TryFindHandledType(Type concreteType, [NotNullWhen(true)] out Type? handlerType)
    {
        handlerType = concreteType.FindInterfaceThatCloses(typeof(IEvent<>))!;
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

        return [];
    }

    public bool IsAdditive => false;
    public List<IMessageTransformation> Transformers { get; } = [];
}

internal class EventUnwrappingMessageRoute<T> : TransformedMessageRoute<IEvent<T>, T> where T : notnull
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
    EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull;
}

public class EventForwardingTransform<TSource> where TSource : notnull
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