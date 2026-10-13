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
using Wolverine.Sqlite;
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

        // Duplicate incoming messages. SQLite reports a unique / primary-key violation through
        // SqliteException.SqliteExtendedErrorCode as SQLITE_CONSTRAINT_PRIMARYKEY (1555) or
        // SQLITE_CONSTRAINT_UNIQUE (2067). The SQL Server 2627 / 2601 pair the Polecat twin tests for
        // has no meaning here.
        //
        // GH-4565: scoped to the inbox table. This used to match on the extended error code alone, which is
        // *any* primary-key or unique-key violation anywhere in the handler's transaction: a duplicate
        // natural key or a unique index on the application's own table was acknowledged and dropped. Not
        // retried, not dead-lettered -- the work never happened and there was no dead letter to find it in.
        options.OnException<Microsoft.Data.Sqlite.SqliteException>(
                SqliteMessageStore.IsDuplicateIncomingEnvelope)
            .Discard();

        options.CodeGeneration.Sources.Add(new FisherBackedPersistenceMarker());

        // GH-4145 (the GH-3001 pattern, ported from Wolverine.Marten): prime the service-location child
        // scope with the handler's outbox-enrolled IDocumentSession so a service-located IDocumentSession
        // / IQuerySession resolves to that same session rather than a separate, un-enrolled one. The
        // frame self-guards (no-op when the chain has no Fisher session).
        options.ScopingFrameSources.Add(() =>
            new PrimeScopedSessionFrame<IDocumentSession, ScopedDocumentSessionHolder>());

        options.CodeGeneration.InsertFirstPersistenceStrategy<FisherPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new SessionVariableSource());
        options.CodeGeneration.Sources.Add(new DocumentOperationsSource());
        options.CodeGeneration.Sources.Add(new EventOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedEventOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedEventStoreOperationsSource());
        options.CodeGeneration.Sources.Add(new SharedDocumentOperationsSource());

        options.Policies.Add<FisherAggregateHandlerStrategy>();

        // GH-4439: pre-populate chain.AncillaryStoreType for [FisherStore]-attributed handlers, so the
        // ancillary-store map built later in WolverineRuntime.HostService sees it. See
        // FisherStoreEagerPolicy for the Phase A vs Phase B ordering trap this addresses (GH-2944), and
        // MartenIntegration/PolecatIntegration for the twins.
        //
        // The policy class shipped with the Fisher integration but was never registered, so it was dead
        // code: [FisherStore] handlers reached codegen with a null AncillaryStoreType. That silently
        // routed interop messages to the MAIN store's inbox, and it is why a [FisherStore] handler taking
        // a natural-key aggregate could not find the store its key is registered on. [Storage(typeof(X))]
        // was unaffected throughout -- core always registers StorageAttributeEagerPolicy -- which is
        // exactly why the existing ancillary coverage, all written against [Storage], never caught it.
        options.Policies.Add<FisherStoreEagerPolicy>();

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);

        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());

        // GH-3947: SQLite has no schemas, so the queue tables take PrefixName as a table name prefix.
        // Opt in only: nothing is stamped unless PrefixName (or the obsolete TransportSchemaName that
        // forwards to it) was set explicitly, so an application that never set it keeps the historical
        // unprefixed queue table names and the messages already sitting in them.
        if (PrefixName != null)
        {
            foreach (var transport in options.Transports.OfType<SqliteTransport>())
            {
                transport.PrefixName = PrefixName;
            }
        }

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

    private string? _prefixName;

    /// <summary>
    ///     GH-3947: an optional prefix for the native SQLite transport's queue and scheduled-message
    ///     tables. SQLite has no schemas, so <c>"reporting"</c> names the tables for an <c>orders</c>
    ///     queue <c>reporting_wolverine_queue_orders</c> and <c>reporting_wolverine_queue_orders_scheduled</c>,
    ///     which lets separate sets of Wolverine queue tables share one Fisher database file.
    /// </summary>
    /// <remarks>
    ///     Opt in: the default of <c>null</c> keeps the historical, unprefixed queue table names, and
    ///     so does SQLite's built-in schema name <c>main</c>. Every application sending to or
    ///     listening on a queue must use the same prefix. Use <see cref="MessageStorageSchemaName"/>
    ///     to prefix Wolverine's inbox and outbox tables as well.
    /// </remarks>
    public string? PrefixName
    {
        get => _prefixName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(PrefixName));
            _prefixName = value?.ToLowerInvariant();
        }
    }

    /// <summary>
    ///     Obsolete. SQLite has no schemas, so this never named a schema; setting it now sets
    ///     <see cref="PrefixName"/>, which prefixes the SQLite queue table names.
    /// </summary>
    [Obsolete("SQLite has no schemas. Use PrefixName, which this property now sets, to prefix the SQLite queue table names. See GH-3947.")]
    public string TransportSchemaName
    {
        get => PrefixName ?? "wolverine_queues";
        set => PrefixName = value;
    }

    private string? _messageStorageSchemaName;

    /// <summary>
    /// Names the Wolverine message store tables inside the Fisher database file. SQLite has no
    /// schemas, so this is a <b>table name prefix</b>: <c>"reporting"</c> gives you
    /// <c>reporting_wolverine_incoming_envelopes</c> and friends, which is what lets logically
    /// separate Wolverine table sets share one file. The default is <c>main</c>, which prefixes
    /// nothing and leaves the tables named exactly as they have always been.
    /// </summary>
    /// <remarks>
    /// GH-3943: this used to reach <c>SqliteMessageStore</c> as a <c>schema.table</c> qualifier, so
    /// any value other than <c>main</c> emitted SQL against a database nothing had ATTACHed and the
    /// host died on the first envelope write with <c>no such table</c>.
    /// </remarks>
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
    private readonly FisherEventRouter _eventRouter;

    internal EventForwardingTransform(FisherEventRouter eventRouter)
    {
        _eventRouter = eventRouter;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        // The subscription is now completed — see FisherEventRouter.BareSubscriptions (GH-4310).
        _eventRouter.BareSubscriptions.Remove(typeof(TSource));

        var transformation = new MessageTransformation<IEvent<TSource>, TDestination>(transformer);
        _eventRouter.Transformers.Add(transformation);
    }
}
