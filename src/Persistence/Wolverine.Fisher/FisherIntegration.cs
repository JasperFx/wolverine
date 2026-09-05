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
        options.OnException<Microsoft.Data.Sqlite.SqliteException>(e =>
                e.SqliteExtendedErrorCode == 1555 || e.SqliteExtendedErrorCode == 2067)
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

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);

        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());

        // GH-3884's transport schema stamping has no Fisher equivalent, and deliberately so:
        // SQLite has no schemas. Wolverine.Sqlite's transport places its queue tables in the one
        // database file, so there is nothing here for TransportSchemaName to align — it is inert on
        // Fisher and kept only for source parity with the Marten and Polecat integrations.
        // MessageStorageSchemaName does steer where the SqliteMessageStore's tables go, and GH-3943
        // made it the naming prefix this comment always claimed it was: it reached the store as a
        // `schema.table` qualifier instead, so any value other than "main" named a database nothing
        // had ATTACHed and the host died on the first envelope write with "no such table".

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
    /// Present for source parity with the Marten and Polecat integrations. <b>It has no effect on a
    /// Fisher host</b>: a Fisher store is a single SQLite file and SQLite has no schemas, so the
    /// SQLite transport names its queue tables <c>wolverine_queue_&lt;name&gt;</c> in that one file
    /// and there is no schema for this to steer. Nothing reads it.
    /// </summary>
    /// <remarks>
    /// GH-3884 removed the equivalent setters from <c>SqlitePersistenceExpression</c> for the same
    /// reason. See also GH-3943, where setting this alongside
    /// <see cref="MessageStorageSchemaName"/> is how the reporter arrived at the message storage bug
    /// — this half was simply inert.
    /// </remarks>
    public string TransportSchemaName
    {
        get => _transportSchemaName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(TransportSchemaName));
            _transportSchemaName = value.ToLowerInvariant();
        }
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
