using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
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
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Persistence.DocumentStores.Codegen;
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
        // GH-4310: a bare SubscribeToEvent<T>() registers nothing — it only opens a
        // transformation that TransformedTo() has to finish. Left alone without event
        // forwarding, the "subscription" is silently dead: no forwarding occurs, the handler
        // never fires, and nothing anywhere says so. Fail at bootstrap with the two real
        // options instead.
        EventRouter.AssertNoBareSubscriptions(UseFastEventForwarding, nameof(UseFastEventForwarding));

        // Duplicate incoming messages. GH-5459 reviewed this and it stays Marten-only: it matches a Postgres
        // primary key violation on Wolverine's incoming envelope table, which is specific to this store, and
        // JasperFx deliberately has no store-neutral command-failure base type to hang it on.
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
        options.ScopingFrameSources.Add(() =>
            new PrimeScopedSessionFrame<IDocumentSession, ScopedDocumentSessionHolder>());

        options.CodeGeneration.InsertFirstPersistenceStrategy<MartenPersistenceFrameProvider>();
        // GH-5141: Wolverine core's session and session-contract sources, given this store's types
        options.CodeGeneration.Sources.Add(new DocumentSessionVariableSource(SessionTypes));
        options.CodeGeneration.Sources.Add(new DocumentSessionContractSource(SessionTypes));

        options.Policies.Add<MartenAggregateHandlerStrategy>();

        // GH-2944: [MartenStore] chains get chain.AncillaryStoreType pre-populated by core's
        // StorageAttributeEagerPolicy (GH-5141), which Wolverine always registers.

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

        // GH-3883: only stamp the transport with schema names the caller actually asked for. This
        // Configure() runs at host build — AFTER an inline UsePostgresqlPersistenceAndTransport(...)
        // in the same options lambda — so assigning unconditionally meant this integration's own
        // DEFAULTS silently overwrote the caller's explicit transport schema. That is invisible and
        // it breaks the data plane rather than startup: a Marten-backed consumer ends up listening
        // on wolverine_queues.wolverine_queue_x while non-Marten publishers write to
        // {configured}.wolverine_queue_x, auto-provision creates both, and nothing is ever delivered.
        var transport = options.Transports.GetOrCreate<PostgresqlTransport>();

        if (_transportSchemaNameIsExplicit)
        {
            transport.TransportSchemaName = TransportSchemaName;
        }

        if (MessageStorageSchemaName.IsNotEmpty())
        {
            transport.MessageStorageSchemaName = MessageStorageSchemaName;
        }
        
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
    ///     Wolverine will try to use the master database from the Marten configuration when possible.
    ///     Wolverine does not dispose this data source; the caller owns it
    /// </summary>
    public NpgsqlDataSource? MasterDataSource { get; set; }

    /// <summary>
    ///     GH-5141: Marten's session types, as Wolverine core's codegen sources need them
    /// </summary>
    internal static readonly DocumentSessionTypes SessionTypes = new()
    {
        StoreName = "Marten",
        DocumentSession = typeof(global::Marten.IDocumentSession),
        QuerySession = typeof(global::Marten.IQuerySession),
        OutboxedSessionFactory = typeof(Wolverine.Marten.Publishing.OutboxedSessionFactory),
        NativeSessionFactory = typeof(global::Marten.ISessionFactory),
        DocumentOperations = typeof(global::Marten.IDocumentOperations),
        EventOperations = typeof(global::Marten.Events.IEventStoreOperations)
    };

    internal EventForwardingRouter EventRouter { get; } = new();

    private string _transportSchemaName = "wolverine_queues";
    private bool _transportSchemaNameIsExplicit;

    /// <summary>
    /// The database schema to place postgres-backed queues. The default is "wolverine_queues".
    /// </summary>
    /// <remarks>
    /// GH-3883: setting this is authoritative — it overwrites whatever the PostgreSQL transport was
    /// configured with, because this integration applies at host build. Leaving it alone leaves the
    /// transport's own configuration (its default, or an explicit
    /// <c>UsePostgresqlPersistenceAndTransport(..., transportSchema: ...)</c>) untouched.
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
    /// The database schema to place the message store tables for Wolverine
    /// The default is to use the same schema as the Marten DocumentStore
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
    /// Optionally override whether to automatically create message database schema objects. Defaults to <see cref="StoreOptions.AutoCreateSchemaObjects"/>
    /// </summary>
    public AutoCreate? AutoCreate { get; set; }

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

internal class MartenOverrides : IConfigureMarten
{
    // Null for the main store (uses the runtime's default message store). Ancillary stores
    // override this with their marker type so the outbox targets the ancillary store's own
    // message store (its configured SchemaName / database). See GH-2887.
    protected virtual Type? StoreType => null;

    // GH-4825. Envelope is registered as a Marten document below, and Marten's DocumentMapping then finds
    // its identity by walking GetProperties()/GetFields() for an "id"/"Id" member. Schema.For<Envelope>()
    // is a static generic over a type this assembly names directly, so ILC keeps the TYPE -- but nothing
    // statically reflects over Envelope.Id, so the trimmer drops the property metadata and a native image
    // fails to start with Marten's InvalidDocumentException ("Could not determine an 'id/Id' field or
    // property for requested document type Wolverine.Envelope"). The Type flow into Marten cannot carry a
    // [DynamicallyAccessedMembers] annotation across a generic like that, so the preservation is declared
    // here, next to the registration that needs it.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Envelope))]
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        options.Events.MessageOutbox = new MartenToWolverineOutbox(services, StoreType);

        // GH-5385: a tracked session hears events committed through sessions Wolverine did not open too
        options.Listeners.Add(new RecordEventsCommittedOutsideAMessage(services));

        // GH-3290: when Wolverine manages the event subscription distribution, it replaces
        // Marten's AddAsyncDaemon() registration outright — Marten hosts no coordinator and
        // starts no agents, but the async projections and subscriptions DO run under
        // Wolverine's distribution. Marten's only knowledge of the daemon state is
        // Projections.AsyncMode; left at Disabled, DocumentStore writes a misleading
        // "The async daemon is disabled ... projections will not be executed" warning at
        // startup. Record the real state: ExternallyManaged keeps Marten's runtime posture
        // identical to Disabled (no Marten-side coordination starts) while suppressing the
        // warning. Only upgrades from Disabled — an explicit user AddAsyncDaemon() choice
        // is never overwritten, regardless of call order relative to IntegrateWithWolverine.
        // Marten's only knowledge of the daemon state is Projections.AsyncMode; left at Disabled,
        // DocumentStore writes a misleading "The async daemon is disabled ... projections will not
        // be executed" warning at startup. Record the real state: ExternallyManaged keeps Marten's
        // runtime posture identical to Disabled (no Marten-side coordination starts) while
        // suppressing the warning. Only upgrades from Disabled — an explicit Solo/HotCold choice is
        // left alone HERE because AddAsyncDaemon applies its mode through ConfigureMarten, which
        // runs in registration order, so a choice made after IntegrateWithWolverine is not yet
        // visible at this point. That combination is invalid and is rejected at host start by
        // ManagedDistributionDaemonModeValidator (GH-3388), where the options are final.
        var integration = services.GetService<MartenIntegration>();
        if (integration is { UseWolverineManagedEventSubscriptionDistribution: true }
            && options.Projections.AsyncMode == DaemonMode.Disabled)
        {
            options.Projections.AsyncMode = DaemonMode.ExternallyManaged;
        }

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
