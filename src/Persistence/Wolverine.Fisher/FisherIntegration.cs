using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ErrorHandling;
using Wolverine.Middleware;
using Wolverine.Fisher.Codegen;
using Wolverine.Fisher.Persistence.Sagas;
using Wolverine.Fisher.Publishing;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Sqlite.Transport;
using Wolverine.Util;
using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Fisher;

public class FisherIntegration : IWolverineExtension, IEventForwarding
{
    private readonly List<Action<WolverineOptions>> _actions = [];

    /// <summary>
    ///     This directs the Fisher integration to try to publish events out of the enrolled outbox
    ///     for a Fisher session on SaveChangesAsync(). This is the "event forwarding" option.
    /// There is no ordering guarantee with this option, but this will distribute event messages
    /// faster than strictly ordered event subscriptions. Default is false
    /// </summary>
    public bool UseFastEventForwarding { get; set; }


    public void Configure(WolverineOptions options)
    {
        // Duplicate incoming messages. SQLite reports a unique / primary-key violation through
        // SqliteException.SqliteExtendedErrorCode as SQLITE_CONSTRAINT_PRIMARYKEY (1555) or
        // SQLITE_CONSTRAINT_UNIQUE (2067). The SQL Server 2627 / 2601 pair the Polecat twin tests for
        // has no meaning here.
        options.OnException<Microsoft.Data.Sqlite.SqliteException>(e =>
                e.SqliteExtendedErrorCode == 1555 || e.SqliteExtendedErrorCode == 2067)
            .Discard();

        options.CodeGeneration.Sources.Add(new FisherBackedPersistenceMarker());

        options.CodeGeneration.InsertFirstPersistenceStrategy<FisherPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new SessionVariableSource());
        options.CodeGeneration.Sources.Add(new DocumentOperationsSource());
        options.CodeGeneration.Sources.Add(new EventOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedEventOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedEventStoreOperationsSource());

        options.Policies.Add<FisherAggregateHandlerStrategy>();

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);

        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());

        // GH-3884's transport schema stamping has no Fisher equivalent, and deliberately so:
        // SQLite has no schemas. Wolverine.Sqlite's transport places its queue tables in the one
        // database file, so there is nothing here for MessageStorageSchemaName / TransportSchemaName
        // to align. Those properties still steer where the SqliteMessageStore's own tables go, which
        // is a naming prefix rather than a schema.

        options.Policies.Add<FisherOpPolicy>();

        options.CodeGeneration.AddContinuationStrategy<Wolverine.Fisher.Requirements.FisherDataRequirementContinuationStrategy>();

        options.CodeGeneration.MethodPreCompilation.Add(new FisherBatchingPolicy());
    }

    /// <summary>
    ///     In the case of Fisher using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    /// </summary>
    public string? MainDatabaseConnectionString { get; set; }

    internal FisherEventRouter EventRouter { get; } = new();

    private string _transportSchemaName = "wolverine_queues";

    /// <summary>
    /// The database schema to place SQLite-backed queues.
    /// </summary>
    /// <remarks>
    /// GH-3884: setting this is authoritative — it overwrites whatever the SQLite transport was
    /// configured with, because this integration applies at host build. Leaving it alone leaves the
    /// transport's own configuration (its default, or an explicit
    /// <c>UseSqlServerPersistenceAndTransport(..., transportSchema: ...)</c>) untouched.
    /// </remarks>
    public string TransportSchemaName
    {
        get => _transportSchemaName;
        set
        {
            _transportSchemaName = value.ToLowerInvariant();
        }
    }

    private string? _messageStorageSchemaName;

    /// <summary>
    /// The database schema to place the message store tables for Wolverine.
    /// The default is "wolverine"
    /// </summary>
    public string? MessageStorageSchemaName
    {
        get => _messageStorageSchemaName;
        set => _messageStorageSchemaName = value?.ToLowerInvariant();
    }

    public EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull
    {
        return new EventForwardingTransform<T>(EventRouter);
    }
}

internal class FisherOverrides : IConfigureFisher
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        // Fisher's DocumentMapping automatically detects IRevisioned types
        // and enables numeric revisions. Wolverine's Saga type uses Version property
        // which is handled by the saga persistence framework.

        // Replace Fisher's default NulloMessageOutbox with the Wolverine bridge so
        // projection authors who call `slice.PublishMessage(...)` from a Fisher
        // RaiseSideEffects override actually have the message delivered through
        // Wolverine after the projection batch's SQL transaction commits. Mirrors
        // the Marten side at MartenIntegration.cs:153. See wolverine#2774.
        options.Events.MessageOutbox = new FisherToWolverineOutbox(services);

        // The Marten and Polecat twins nudge DaemonSettings.AsyncMode to ExternallyManaged when
        // Wolverine takes over hosting the daemon. Wolverine.Fisher never does - see the note in
        // WolverineOptionsFisherExtensions on why subscription distribution has no meaning over a
        // single SQLite file - so Fisher's own AddAsyncDaemon() choice is left exactly as the caller
        // made it.
    }
}

internal class EventWrapperForwarder : IHandledTypeRule
{
    public bool TryFindHandledType(Type concreteType, [NotNullWhen(true)] out Type? handlerType)
    {
        handlerType = concreteType.FindInterfaceThatCloses(typeof(IEvent<>));
        return handlerType != null;
    }
}

internal class FisherEventRouter : IMessageRouteSource
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

            var forEventType = runtime.RoutingFor(eventType).Routes.Select(route =>
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
    private readonly FisherEventRouter _eventRouter;

    internal EventForwardingTransform(FisherEventRouter eventRouter)
    {
        _eventRouter = eventRouter;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        var transformation = new MessageTransformation<IEvent<TSource>, TDestination>(transformer);
        _eventRouter.Transformers.Add(transformation);
    }
}
