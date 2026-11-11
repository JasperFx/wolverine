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
using Wolverine.Persistence.EventSourcing;
using Wolverine.Persistence.DocumentStores.Codegen;
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

    /// <summary>
    ///     Run this store's async projections and subscriptions as Wolverine agents
    ///     (<see cref="Wolverine.Runtime.Agents.EventSubscriptionAgentFamily" />), the way
    ///     <c>UseWolverineManagedEventSubscriptionDistribution</c> does for Marten and Polecat. A Fisher store is
    ///     one SQLite file, so this does not spread projections over several nodes. What it buys is one operator
    ///     surface across the three stores: tooling such as CritterWatch pauses, restarts, rebuilds and rewinds a
    ///     Fisher projection through the agent family exactly as it does a Marten one. Replaces Fisher's own
    ///     <c>AddAsyncDaemon(DaemonMode.Solo)</c>, which the host refuses alongside it. Applies to ancillary
    ///     Fisher stores as well. Default is false.
    /// </summary>
    public bool UseWolverineManagedEventSubscriptionDistribution { get; set; }


    public void Configure(WolverineOptions options)
    {
        // GH-4310 (mirrored from Wolverine.Marten): a bare SubscribeToEvent<T>() registers
        // nothing — it only opens a transformation that TransformedTo() has to finish. Left alone
        // without event forwarding, the "subscription" is silently dead: no forwarding occurs, the
        // handler never fires, and nothing anywhere says so. Fail at bootstrap with the two real
        // options instead.
        EventRouter.AssertNoBareSubscriptions(UseFastEventForwarding, nameof(UseFastEventForwarding));

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
        // GH-5141: Wolverine core's session and session-contract sources, given this store's types
        options.CodeGeneration.Sources.Add(new DocumentSessionVariableSource(SessionTypes));
        options.CodeGeneration.Sources.Add(new DocumentSessionContractSource(SessionTypes));

        options.Policies.Add<FisherAggregateHandlerStrategy>();

        // GH-4439: [FisherStore] chains get chain.AncillaryStoreType pre-populated by core's
        // StorageAttributeEagerPolicy (GH-5141), which Wolverine always registers. The Fisher copy of that
        // policy once shipped unregistered, which silently routed interop messages to the MAIN store's
        // inbox -- one policy over IAncillaryStoreAttribute leaves no per-store copy to forget.

        options.Discovery.CustomizeHandlerDiscovery(x =>
        {
            x.Includes.WithAttribute<AggregateHandlerAttribute>();
        });

        options.PublishWithMessageRoutingSource(EventRouter);

        options.Policies.ForwardHandledTypes(new EventWrapperForwarder());

        // GH-3947: SQLite has no schemas, so the queue tables take PrefixName as a table name prefix.
        // Opt in only: nothing is stamped unless PrefixName was set explicitly, so an application that
        // never set it keeps the historical unprefixed queue table names and the messages already in them.
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

    /// <summary>
    ///     GH-5141: Fisher's session types, as Wolverine core's codegen sources need them
    /// </summary>
    internal static readonly DocumentSessionTypes SessionTypes = new()
    {
        StoreName = "Fisher",
        DocumentSession = typeof(global::Fisher.IDocumentSession),
        QuerySession = typeof(global::Fisher.IQuerySession),
        OutboxedSessionFactory = typeof(Wolverine.Fisher.Publishing.OutboxedSessionFactory),
        NativeSessionFactory = typeof(global::Fisher.ISessionFactory),
        DocumentOperations = typeof(global::Fisher.IDocumentOperations),
        EventOperations = typeof(global::Fisher.Events.EventOperations)
    };

    internal EventForwardingRouter EventRouter { get; } = new();

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
        EventRouter.StartTransformation<T>();
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

        // GH-5390: a tracked session hears events committed through sessions Wolverine did not open too
        options.Listeners.Add(new RecordEventsCommittedOutsideAMessage(services));

        // The Marten and Polecat twins nudge DaemonSettings.AsyncMode to ExternallyManaged when
        // Wolverine takes over hosting the daemon. Fisher has no such setting: AddAsyncDaemon() registers
        // its hosted daemon or nothing, so under UseWolverineManagedEventSubscriptionDistribution (GH-5094)
        // ManagedDistributionDaemonModeValidator refuses an AddAsyncDaemon(DaemonMode.Solo) at host start.
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
    private readonly EventForwardingRouter _eventRouter;

    internal EventForwardingTransform(EventForwardingRouter eventRouter)
    {
        _eventRouter = eventRouter;
    }

    public void TransformedTo<TDestination>(Func<IEvent<TSource>, TDestination> transformer)
    {
        _eventRouter.CompleteTransformation(transformer);
    }
}
