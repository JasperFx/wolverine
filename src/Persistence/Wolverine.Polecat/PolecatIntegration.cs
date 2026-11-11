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
using Wolverine.Persistence.EventSourcing;
using Wolverine.Persistence.DocumentStores.Codegen;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.SqlServer.Persistence;
using Wolverine.SqlServer.Transport;
using Wolverine.Util;
using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Polecat;

// GH-5514: the per-store IEventForwarding stays implemented until 8.0, for compatibility
#pragma warning disable CS0618
public class PolecatIntegration : IWolverineExtension, IEventForwarding, Wolverine.Persistence.EventSourcing.Forwarding.IEventForwarding
#pragma warning restore CS0618
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
        EventRouter.AssertNoBareSubscriptions(UseFastEventForwarding, nameof(UseFastEventForwarding));

        // Duplicate incoming messages - SQL Server uses unique constraint violations.
        //
        // GH-4565: scoped to the inbox table. This used to match on e.Number == 2627 || e.Number == 2601
        // alone, which is *any* primary-key or unique-key violation anywhere in the handler's transaction:
        // a duplicate natural key or a unique index on the application's own table was acknowledged and
        // dropped. Not retried, not dead-lettered -- the work never happened and there was no dead letter
        // to find it in, which is the hardest failure mode there is to diagnose after the fact.
        options.OnException<Microsoft.Data.SqlClient.SqlException>(
                SqlServerMessageStore.IsDuplicateIncomingEnvelope)
            .Discard();

        options.CodeGeneration.Sources.Add(new PolecatBackedPersistenceMarker());

        // GH-4145 (the GH-3001 pattern, ported from Wolverine.Marten): prime the service-location child
        // scope with the handler's outbox-enrolled IDocumentSession so a service-located IDocumentSession
        // / IQuerySession resolves to that same session rather than a separate, un-enrolled one. The
        // frame self-guards (no-op when the chain has no Polecat session).
        options.ScopingFrameSources.Add(() =>
            new PrimeScopedSessionFrame<IDocumentSession, ScopedDocumentSessionHolder>());

        options.CodeGeneration.InsertFirstPersistenceStrategy<PolecatPersistenceFrameProvider>();
        // GH-5141: Wolverine core's session and session-contract sources, given this store's types
        options.CodeGeneration.Sources.Add(new DocumentSessionVariableSource(SessionTypes));
        options.CodeGeneration.Sources.Add(new DocumentSessionContractSource(SessionTypes));

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

        // GH-3109: [PolecatStore] chains get chain.AncillaryStoreType pre-populated by core's
        // StorageAttributeEagerPolicy (GH-5141), which Wolverine always registers.

        options.CodeGeneration.AddContinuationStrategy<Wolverine.Polecat.Requirements.PolecatDataRequirementContinuationStrategy>();

        options.CodeGeneration.MethodPreCompilation.Add(new PolecatBatchingPolicy());
    }

    /// <summary>
    ///     In the case of Polecat using a database per tenant, you may wish to
    ///     explicitly determine the master database for Wolverine where Wolverine will store node and envelope information.
    ///     This does not have to be one of the tenant databases
    /// </summary>
    public string? MainDatabaseConnectionString { get; set; }

    /// <summary>
    ///     GH-5141: Polecat's session types, as Wolverine core's codegen sources need them
    /// </summary>
    internal static readonly DocumentSessionTypes SessionTypes = new()
    {
        StoreName = "Polecat",
        DocumentSession = typeof(global::Polecat.IDocumentSession),
        QuerySession = typeof(global::Polecat.IQuerySession),
        OutboxedSessionFactory = typeof(Wolverine.Polecat.Publishing.OutboxedSessionFactory),
        NativeSessionFactory = typeof(global::Polecat.ISessionFactory),
        DocumentOperations = typeof(global::Polecat.IDocumentOperations),
        EventOperations = typeof(global::Polecat.Events.IEventOperations)
    };

    internal EventForwardingRouter EventRouter { get; } = new();

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
    /// <see cref="Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform{TSource}.TransformedTo{TDestination}"/> to publish the
    /// transformed message under Wolverine's normal routing rules. ⚠️ This is not an on-switch for
    /// event forwarding, and a bare call registers nothing (GH-4310 — the bootstrap rejects it):
    /// to simply have Wolverine handlers receive an event after commit, set
    /// <see cref="UseFastEventForwarding"/> = true instead.
    /// </summary>
    // GH-5514: the signature keeps the per-store type until 8.0; the value is already the core type
#pragma warning disable CS0618
    public EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull
    {
        EventRouter.StartTransformation<T>();
        return new EventForwardingTransform<T>(EventRouter);
    }
#pragma warning restore CS0618

    // GH-5514: the core forwarding API. The public SubscribeToEvent<T>() above keeps its signature until 8.0, and
    // what it returns is already the core type
    Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform<T> Wolverine.Persistence.EventSourcing.Forwarding.IEventForwarding.SubscribeToEvent<T>()
    {
        return SubscribeToEvent<T>();
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

        // A session the application opened itself and then enrolled in IPolecatOutbox can't be given a listener
        // of its own, so the store carries this one for it
        options.Listeners.Add(new FlushEnrolledOutboxOnCommit());

        // GH-5390: a tracked session hears events committed through sessions Wolverine did not open too
        options.Listeners.Add(new RecordEventsCommittedOutsideAMessage(services));

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

[Obsolete("Use Wolverine.Persistence.EventSourcing.Forwarding.IEventForwarding; this type will be removed in Wolverine 8.0")]
public interface IEventForwarding
{
    /// <summary>
    /// Subscribe to an event, but with a transformation. The transformed message will be
    /// published to Wolverine with its normal routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    EventForwardingTransform<T> SubscribeToEvent<T>() where T : notnull;
}

/// <summary>
/// GH-5514: kept for compatibility. It is a <see cref="Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform{TSource}" />, so
/// the value <see cref="PolecatIntegration.SubscribeToEvent{T}" /> returns can be used as the core type already.
/// </summary>
[Obsolete("Use Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform<TSource>; this type will be removed in Wolverine 8.0")]
public class EventForwardingTransform<TSource> : Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform<TSource> where TSource : notnull
{
    internal EventForwardingTransform(EventForwardingRouter eventRouter) : base(eventRouter)
    {
    }
}
