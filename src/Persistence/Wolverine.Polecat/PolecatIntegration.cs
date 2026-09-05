using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Polecat;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ErrorHandling;
using Wolverine.Middleware;
using Wolverine.Polecat.Codegen;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Polecat.Publishing;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.SqlServer.Transport;
using Wolverine.Util;
using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Polecat;

public class PolecatIntegration : IWolverineExtension, IEventForwarding
{
    private readonly List<Action<WolverineOptions>> _actions = [];

    /// <summary>
    ///     This directs the Polecat integration to try to publish events out of the enrolled outbox
    ///     for a Polecat session on SaveChangesAsync(). This is the "event forwarding" option.
    /// There is no ordering guarantee with this option, but this will distribute event messages
    /// faster than strictly ordered event subscriptions. Default is false
    /// </summary>
    public bool UseFastEventForwarding { get; set; }

    /// <summary>
    ///     Use Wolverine's agent framework to manage the distribution of Polecat event
    ///     subscription processing across nodes in a cluster. Default is false.
    /// </summary>
    public bool UseWolverineManagedEventSubscriptionDistribution { get; set; }

    public void Configure(WolverineOptions options)
    {
        // GH-4310 (mirrored from Wolverine.Marten): a bare SubscribeToEvent<T>() registers
        // nothing — it only opens a transformation that TransformedTo() has to finish. Left alone
        // without event forwarding, the "subscription" is silently dead: no forwarding occurs, the
        // handler never fires, and nothing anywhere says so. Fail at bootstrap with the two real
        // options instead.
        if (EventRouter.BareSubscriptions.Any() && !UseFastEventForwarding)
        {
            var names = EventRouter.BareSubscriptions.Select(x => x.Name).Join(", ");
            throw new InvalidOperationException(
                $"SubscribeToEvent<T>() was called for [{names}] but nothing completes the subscription, so these events would never reach a handler. " +
                $"SubscribeToEvent<T>() only defines a transformation of a forwarded event — either finish it with .TransformedTo(...), " +
                $"or, to simply have Wolverine handlers receive the event after commit, set {nameof(UseFastEventForwarding)} = true on this integration instead.");
        }

        // Duplicate incoming messages - SQL Server uses unique constraint violations
        options.OnException<Microsoft.Data.SqlClient.SqlException>(e =>
            {
                // Unique key violation on incoming table
                return e.Number == 2627 || e.Number == 2601;
            })
            .Discard();

        options.CodeGeneration.Sources.Add(new PolecatBackedPersistenceMarker());

        // GH-4145 (the GH-3001 pattern, ported from Wolverine.Marten): prime the service-location child
        // scope with the handler's outbox-enrolled IDocumentSession so a service-located IDocumentSession
        // / IQuerySession resolves to that same session rather than a separate, un-enrolled one. The
        // frame self-guards (no-op when the chain has no Polecat session).
        options.ScopingFrameSources.Add(() =>
            new PrimeScopedSessionFrame<IDocumentSession, ScopedDocumentSessionHolder>());

        options.CodeGeneration.InsertFirstPersistenceStrategy<PolecatPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new SessionVariableSource());
        options.CodeGeneration.Sources.Add(new DocumentOperationsSource());
        options.CodeGeneration.Sources.Add(new EventOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedEventOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedEventStoreOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedDocumentOperationsSource());

        options.Policies.Add<PolecatAggregateHandlerStrategy>();

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);

        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());

        // GH-3884 (the mirror image of GH-3883 on the Marten side): only stamp the SQL Server
        // transport with schema names the caller actually asked for on this integration. This
        // Configure() runs at host build — AFTER an inline UseSqlServerPersistenceAndTransport(...)
        // in the same options lambda — so an explicit assignment here is authoritative over the
        // transport's own configuration, while leaving the property alone leaves the transport
        // untouched (its default, or an explicit
        // UseSqlServerPersistenceAndTransport(..., transportSchema: ...)). Unlike the Marten twin,
        // this does NOT create the transport when none is registered: SqlServerTransport requires
        // connection settings at construction, and with no SQL Server-backed queue endpoints there
        // are no transport tables to place.
        foreach (var transport in options.Transports.OfType<SqlServerTransport>())
        {
            if (_transportSchemaNameIsExplicit)
            {
                transport.TransportSchemaName = TransportSchemaName;
            }

            if (MessageStorageSchemaName.IsNotEmpty())
            {
                // Keep the transport's envelope-table references (its dequeue/send SQL targets
                // wolverine_incoming_envelopes / wolverine_outgoing_envelopes by schema) aligned
                // with where this integration actually places the message storage. Mirrors
                // MartenIntegration.Configure().
                transport.MessageStorageSchemaName = MessageStorageSchemaName;
            }
        }

        options.Policies.Add<PolecatOpPolicy>();

        // GH-3109: pre-populate chain.AncillaryStoreType for [PolecatStore]-attributed handlers so the
        // message-type-to-ancillary-store map built later in WolverineRuntime.HostService sees it.
        // Mirrors Marten's MartenStoreEagerPolicy; see PolecatStoreEagerPolicy for the Phase-A vs
        // Phase-B ordering trap this addresses.
        options.Policies.Add<PolecatStoreEagerPolicy>();

        options.CodeGeneration.AddContinuationStrategy<Wolverine.Polecat.Requirements.PolecatDataRequirementContinuationStrategy>();

        options.CodeGeneration.MethodPreCompilation.Add(new PolecatBatchingPolicy());
    }

    /// <summary>
    ///     In the case of Polecat using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    /// </summary>
    public string? MainDatabaseConnectionString { get; set; }

    internal PolecatEventRouter EventRouter { get; } = new();

    private string _transportSchemaName = "wolverine_queues";
    private bool _transportSchemaNameIsExplicit;

    /// <summary>
    /// The database schema to place SQL Server-backed queues.
    /// </summary>
    /// <remarks>
    /// GH-3884: setting this is authoritative — it overwrites whatever the SQL Server transport was
    /// configured with, because this integration applies at host build. Leaving it alone leaves the
    /// transport's own configuration (its default, or an explicit
    /// <c>UseSqlServerPersistenceAndTransport(..., transportSchema: ...)</c>) untouched.
    /// </remarks>
    public string TransportSchemaName
    {
        get => _transportSchemaName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(TransportSchemaName));
            _transportSchemaName = value.ToLowerInvariant();
            _transportSchemaNameIsExplicit = true;
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
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(MessageStorageSchemaName));
            _messageStorageSchemaName = value?.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Define a <em>transformation</em> of a forwarded event: complete the returned builder with
    /// <see cref="EventForwardingTransform{TSource}.TransformedTo{TDestination}"/> to publish the
    /// transformed message under Wolverine's normal routing rules. ⚠️ This is not an on-switch for
    /// event forwarding, and a bare call registers nothing (GH-4310 — the bootstrap rejects it):
    /// to simply have Wolverine handlers receive an event after commit, set
    /// <see cref="UseFastEventForwarding"/> = true instead.
    /// </summary>
    public EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull
    {
        EventRouter.BareSubscriptions.Add(typeof(T));
        return new EventForwardingTransform<T>(EventRouter);
    }
}

internal class PolecatOverrides : IConfigurePolecat
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        // Polecat's DocumentMapping automatically detects IRevisioned types
        // and enables numeric revisions. Wolverine's Saga type uses Version property
        // which is handled by the saga persistence framework.

        // Replace Polecat's default NulloMessageOutbox with the Wolverine bridge so
        // projection authors who call `slice.PublishMessage(...)` from a Polecat
        // RaiseSideEffects override actually have the message delivered through
        // Wolverine after the projection batch's SQL transaction commits. Mirrors
        // the Marten side at MartenIntegration.cs:153. See wolverine#2774.
        options.Events.MessageOutbox = new PolecatToWolverineOutbox(services);

        // GH-3290 (Polecat parity with the Marten side): when Wolverine manages the event
        // subscription distribution, it replaces Polecat's own daemon/coordinator hosting
        // outright, but the store's only knowledge of the daemon state is
        // DaemonSettings.AsyncMode. Record the real state: ExternallyManaged keeps the
        // store's runtime posture identical to Disabled (nothing Polecat-hosted starts)
        // while telling any AsyncMode reader that the async projections DO run. Only
        // upgrades from Disabled — an explicit user AddAsyncDaemon()/AddProjectionCoordinator()
        // choice is never overwritten, regardless of call order relative to IntegrateWithWolverine.
        var integration = services.GetService<PolecatIntegration>();
        if (integration is { UseWolverineManagedEventSubscriptionDistribution: true }
            && options.DaemonSettings.AsyncMode == DaemonMode.Disabled)
        {
            options.DaemonSettings.AsyncMode = DaemonMode.ExternallyManaged;
        }
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

internal class PolecatEventRouter : IMessageRouteSource
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

    /// <summary>
    /// GH-4310: SubscribeToEvent&lt;T&gt;() calls whose transformation was never completed with
    /// TransformedTo(). Checked at bootstrap — bare entries with no event forwarding enabled are
    /// a configuration error, because nothing would ever deliver those events.
    /// </summary>
    internal List<Type> BareSubscriptions { get; } = [];
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
    private readonly PolecatEventRouter _eventRouter;

    internal EventForwardingTransform(PolecatEventRouter eventRouter)
    {
        _eventRouter = eventRouter;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        // The subscription is now completed — see PolecatEventRouter.BareSubscriptions (GH-4310).
        _eventRouter.BareSubscriptions.Remove(typeof(TSource));

        var transformation = new MessageTransformation<IEvent<TSource>, TDestination>(transformer);
        _eventRouter.Transformers.Add(transformation);
    }
}
