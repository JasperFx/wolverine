using System.Reflection;
using System.Text.Json.Serialization;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration.Capabilities;

public class ServiceCapabilities : OptionsDescription
{
    [JsonConstructor]
    public ServiceCapabilities()
    {
    }

    public ServiceCapabilities(WolverineOptions options)
    {
        // Explicitly select only properties useful for CritterWatch monitoring —
        // no Reflection, no noise from WolverineOptions internal plumbing
        Subject = "Wolverine.WolverineOptions";

        Version = (options.ApplicationAssembly ?? Assembly.GetEntryAssembly())!.GetName().Version?.ToString()!;
        WolverineVersion = options.GetType().Assembly.GetName().Version?.ToString();
        DurabilitySettings = options.Durability.ToDescription();

        AddValue(nameof(options.ServiceName), options.ServiceName);
        AddValue(nameof(options.DefaultExecutionTimeout), options.DefaultExecutionTimeout);
        AddValue(nameof(options.DefaultRemoteInvocationTimeout), options.DefaultRemoteInvocationTimeout);
        AddValue(nameof(options.DisableAllExternalListeners), options.DisableAllExternalListeners);
        AddValue(nameof(options.EnableRemoteInvocation), options.EnableRemoteInvocation);
        AddValue(nameof(options.EnableAutomaticFailureAcks), options.EnableAutomaticFailureAcks);
        AddValue(nameof(options.EnableRelayOfUserName), options.EnableRelayOfUserName);
        AddValue(nameof(options.ServiceLocationPolicy), options.ServiceLocationPolicy);
        AddValue("MetricsMode", options.Metrics.Mode);
        AddValue("MetricsSamplingPeriod", options.Metrics.SamplingPeriod);

        // Surface the WolverineOptions.Tracking flags so CritterWatch can render which
        // opt-in tracing diagnostics this service has enabled. Each flag matches its
        // property name on TrackingOptions (no rename through this layer).
        AddValue(nameof(options.Tracking.EnableMessageCausationTracking), options.Tracking.EnableMessageCausationTracking);
        AddValue(nameof(options.Tracking.HandlerExecutionDiagnosticsEnabled), options.Tracking.HandlerExecutionDiagnosticsEnabled);
        AddValue(nameof(options.Tracking.DeserializationSpanEnabled), options.Tracking.DeserializationSpanEnabled);
        AddValue(nameof(options.Tracking.OutboxDiagnosticsEnabled), options.Tracking.OutboxDiagnosticsEnabled);
    }

    public DateTimeOffset Evaluated { get; set; } = DateTimeOffset.UtcNow;

    public string Version { get; set; } = null!;

    public string? WolverineVersion { get; set; }

    public List<EventStoreUsage> EventStores { get; set; } = [];

    /// <summary>
    /// Diagnostic snapshots of every <c>IDocumentStore</c> registered in the
    /// service container — Marten and Polecat alike. Populated by walking
    /// <see cref="IDocumentStoreUsageSource"/> services through DI; mirrors
    /// the <see cref="EventStores"/> collection so monitoring tools
    /// (CritterWatch) can render document-side configuration the same way.
    /// </summary>
    public List<DocumentStoreUsage> DocumentStores { get; set; } = [];

    /// <summary>
    /// Diagnostic snapshots of every EF Core <c>DbContext</c> registered in
    /// the service container, populated by walking
    /// <see cref="IDbContextUsageSource"/> services through DI. Mirrors
    /// <see cref="DocumentStores"/> on the EF Core side so CritterWatch's
    /// Storage tab can render the third subsection alongside Event Stores
    /// and Document Stores. (#102)
    /// </summary>
    public List<DbContextUsage> DbContexts { get; set; } = [];

    public List<MessageDescriptor> Messages { get; set; } = [];

    /// <summary>
    /// One <see cref="SagaTypeDescriptor"/> per concrete <see cref="Saga"/>
    /// state class discovered in the handler graph. Each descriptor lists
    /// the messages that <em>start</em> a saga (handlers named
    /// <c>Start</c> / <c>StartOrHandle</c> on the saga type) versus those
    /// that <em>continue</em> an existing one (<c>Orchestrate</c> /
    /// <c>Handle</c> / <c>NotFound</c>), plus a <c>StorageProvider</c>
    /// tag (e.g. <c>Marten</c>, <c>EntityFrameworkCore</c>) so monitoring
    /// tools can group sagas by backing store. The provider tag is
    /// resolved by asking each registered
    /// <see cref="IPersistenceFrameProvider"/> whether it can persist the
    /// saga state type — the same mechanism the saga handler pipeline
    /// uses at codegen time to decide which storage handles each saga.
    /// </summary>
    public List<SagaTypeDescriptor> SagaTypes { get; set; } = [];

    public List<MessageStore> MessageStores { get; set; } = [];

    /// <summary>
    /// Diagnostic snapshots of every Wolverine.HTTP graph in this
    /// process — populated by walking <see cref="IHttpGraphUsageSource"/>
    /// services through DI. Mirrors <see cref="DocumentStores"/> on the
    /// HTTP side (#84). Empty when no Wolverine.HTTP graph is loaded.
    /// </summary>
    public List<HttpGraphUsage> HttpGraphs { get; set; } = [];

    /// <summary>
    /// Diagnostic snapshots of non-Wolverine ASP.NET Core endpoints
    /// (Minimal API, MVC, Razor Pages, SignalR, …) populated by
    /// <c>Wolverine.CritterWatch.Http</c> when the host opted into it
    /// via <c>services.AddCritterWatchHttp()</c>. Empty when the
    /// integration package isn't loaded — pure-Wolverine workers and
    /// console hosts incur no ASP.NET Core dependency.
    /// </summary>
    public List<AspNetEndpointDescriptor> AspNetEndpoints { get; set; } = [];

    public List<EndpointDescriptor> MessagingEndpoints { get; set; } = [];

    public DatabaseCardinality MessageStoreCardinality { get; set; } = DatabaseCardinality.None;

    public List<BrokerDescription> Brokers { get; set; } = [];

    public OptionsDescription? DurabilitySettings { get; set; }

    /// <summary>
    /// Additional capability descriptions contributed by extension frameworks
    /// (e.g. Wolverine.HTTP) via ICapabilityDescriptor implementations.
    /// </summary>
    public List<OptionsDescription> AdditionalCapabilities { get; set; } = [];

    /// <summary>
    ///     Uri for sending command messages to this service
    /// </summary>
    public Uri? SystemControlUri { get; set; }

    public static async Task<ServiceCapabilities> ReadFrom(IWolverineRuntime runtime, Uri? systemControlUri,
        CancellationToken token)
    {
        var capabilities = new ServiceCapabilities(runtime.Options)
        {
            SystemControlUri = systemControlUri
        };

        readTransports(runtime, capabilities);

        await readMessageStores(runtime, capabilities);

        await readEventStores(runtime, token, capabilities);

        await readDocumentStores(runtime, token, capabilities);

        await readDbContexts(runtime, token, capabilities);

        await readHttpGraphs(runtime, token, capabilities);

        readAspNetEndpoints(runtime, capabilities);

        readMessageTypes(runtime, capabilities);

        readEndpoints(runtime, capabilities);

        readSagas(runtime, capabilities);

        readAdditionalCapabilities(runtime, capabilities);

        return capabilities;
    }

    /// <summary>
    /// Mirror of <see cref="readDocumentStores"/> for Wolverine HTTP
    /// graphs. Walks every <see cref="IHttpGraphUsageSource"/>
    /// registered in DI (Wolverine.Http auto-registers a single source
    /// when <c>MapWolverineEndpoints()</c> is called) and asks each one
    /// for a snapshot. Sources that return null (transient init) are
    /// silently skipped.
    /// </summary>
    private static async Task readHttpGraphs(IWolverineRuntime runtime, CancellationToken token,
        ServiceCapabilities capabilities)
    {
        var sources = runtime.Services.GetServices<IHttpGraphUsageSource>();
        var seen = new HashSet<Uri>();
        var list = new List<HttpGraphUsage>();
        foreach (var source in sources)
        {
            if (!seen.Add(source.Subject)) continue;

            var usage = await source.TryCreateUsage(runtime.Services, token);
            if (usage != null)
            {
                list.Add(usage);
            }
        }

        capabilities.HttpGraphs.AddRange(list.OrderBy(x => x.SubjectUri.ToString()));
    }

    /// <summary>
    /// Walk every <see cref="IAspNetEndpointDescriptorSource"/> in DI —
    /// implemented in <c>Wolverine.CritterWatch.Http</c> when the host
    /// opted into it. Pure-Wolverine workers won't have any registered;
    /// the collection stays empty.
    /// </summary>
    private static void readAspNetEndpoints(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var sources = runtime.Services.GetServices<IAspNetEndpointDescriptorSource>();
        var list = new List<AspNetEndpointDescriptor>();
        foreach (var source in sources)
        {
            list.AddRange(source.Endpoints);
        }

        capabilities.AspNetEndpoints.AddRange(list.OrderBy(x => x.Route + "::" + string.Join(",", x.HttpMethods)));
    }

    private static void readEndpoints(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        foreach (var endpoint in runtime.Options.Transports.AllEndpoints().OrderBy(x => x.Uri.ToString()))
        {
            if (endpoint.Role == EndpointRole.System) continue;
            capabilities.MessagingEndpoints.Add(new EndpointDescriptor(endpoint));
        }
    }

    private static void readMessageTypes(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var messageTypes = runtime.Options.Discovery.FindAllMessages(runtime.Options.HandlerGraph);
        foreach (var messageType in messageTypes.OrderBy(x => x.FullNameInCode()))
        {
            if (messageType.IsSystemMessageType()) continue;
            capabilities.Messages.Add(new MessageDescriptor(messageType, runtime));
        }
    }

    /// <summary>
    /// Walk every <see cref="SagaChain"/> on the handler graph and emit
    /// one <see cref="SagaTypeDescriptor"/> per concrete saga state type.
    /// Messages are split into starting vs continuing by
    /// <see cref="SagaMessageBuckets"/>, the same helper every
    /// <see cref="ISagaStoreDiagnostics"/> implementation uses — so the
    /// per-storage descriptors and this host-wide capabilities snapshot
    /// always agree on the bucket assignments. The
    /// <c>StorageProvider</c> tag is resolved by asking each registered
    /// <see cref="IPersistenceFrameProvider"/> whether it can persist
    /// the saga state type, identical to what the saga handler pipeline
    /// picks at codegen time.
    /// </summary>
    private static void readSagas(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var sagaChains = SagaMessageBuckets.saga_chains(runtime.Options.HandlerGraph).ToArray();
        if (sagaChains.Length == 0) return;

        var providers = runtime.Options.CodeGeneration.PersistenceProviders();
        var container = runtime.Options.HandlerGraph.Container;

        var sagaTypes = sagaChains
            .Where(c => c.Handlers.Any(h => h.HandlerType.CanBeCastTo<Saga>()))
            .Select(c => c.SagaType)
            .Distinct()
            .OrderBy(t => t.FullNameInCode());

        foreach (var sagaType in sagaTypes)
        {
            var (starting, continuing) = SagaMessageBuckets.For(sagaType, runtime.Options.HandlerGraph);
            var storageProvider = resolveStorageProvider(sagaType, providers, container);

            capabilities.SagaTypes.Add(new SagaTypeDescriptor(
                TypeDescriptor.For(sagaType),
                starting,
                continuing,
                storageProvider));
        }
    }

    /// <summary>
    /// Tag a saga state type with the persistence-provider name that
    /// owns it at runtime. Walks the registered
    /// <see cref="IPersistenceFrameProvider"/>s — same precedence the
    /// saga code-gen uses — and returns a stable, human-readable string
    /// for the first one that claims the type. Falls back to
    /// <c>"InMemory"</c> when no provider is registered (a common shape
    /// for in-process tests).
    /// </summary>
    private static string resolveStorageProvider(Type sagaType, IReadOnlyList<IPersistenceFrameProvider> providers, IServiceContainer container)
    {
        if (providers.Count == 0) return "InMemory";

        foreach (var provider in providers)
        {
            try
            {
                if (provider.CanPersist(sagaType, container, out _))
                {
                    return providerLabel(provider);
                }
            }
            catch
            {
                // CanPersist may probe DI for a real session/context — if
                // resolution fails (transient registration that needs a
                // scope, etc) treat that provider as not-applicable and
                // keep walking. Storage tagging is diagnostic-only and
                // must not raise.
            }
        }

        return "InMemory";
    }

    private static string providerLabel(IPersistenceFrameProvider provider)
    {
        // Type-name-driven so each provider package owns its own label
        // without Wolverine.Core needing to know the concrete types.
        // Strip the "PersistenceFrameProvider" suffix Marten/EFCore/RavenDb
        // all use to land on a clean, UI-friendly tag.
        var name = provider.GetType().Name;
        const string suffix = "PersistenceFrameProvider";
        if (name.EndsWith(suffix, StringComparison.Ordinal))
            name = name[..^suffix.Length];
        return name.Length == 0 ? "InMemory" : name;
    }

    public const string EventSubscriptionAgentScheme = "event-subscriptions";

    private static async Task readEventStores(IWolverineRuntime runtime, CancellationToken token,
        ServiceCapabilities capabilities)
    {
        var eventStores = runtime.Services.GetServices<IEventStore>();
        var storeList = new List<EventStoreUsage>();
        foreach (var eventStore in eventStores)
        {
            var eventStoreUsage = await eventStore.TryCreateUsage(token);
            if (eventStoreUsage != null)
            {
                eventStoreUsage.PopulateAgentUris(EventSubscriptionAgentScheme, eventStore.Identity);
                storeList.Add(eventStoreUsage);
            }
        }

        capabilities.EventStores.AddRange(storeList.OrderBy(x => x.SubjectUri.ToString()));
    }

    /// <summary>
    /// Mirror of <see cref="readEventStores"/> for the document side. Walks
    /// every <see cref="IDocumentStoreUsageSource"/> registered in DI (Marten
    /// stores satisfy this via <c>IDocumentStore</c>; Polecat stores too), and
    /// asks each one for a <see cref="DocumentStoreUsage"/> snapshot. Stores
    /// that return null (transient-init failure) are silently skipped — same
    /// permissive policy as the event-store path.
    /// </summary>
    private static async Task readDocumentStores(IWolverineRuntime runtime, CancellationToken token,
        ServiceCapabilities capabilities)
    {
        var stores = runtime.Services.GetServices<IDocumentStoreUsageSource>();
        var seen = new HashSet<Uri>();
        var storeList = new List<DocumentStoreUsage>();
        foreach (var store in stores)
        {
            // Marten stores typically also register as IEventStore on the same
            // instance — once Wolverine boots both interfaces resolve to the
            // same concrete object. Dedupe by Subject URI so we don't double-
            // count when a store wears both hats.
            if (!seen.Add(store.Subject)) continue;

            var usage = await store.TryCreateUsage(token);
            if (usage != null)
            {
                storeList.Add(usage);
            }
        }

        capabilities.DocumentStores.AddRange(storeList.OrderBy(x => x.SubjectUri.ToString()));
    }

    /// <summary>
    /// Mirror of <see cref="readDocumentStores"/> for EF Core. Walks every
    /// <see cref="IDbContextUsageSource"/> registered in DI (each
    /// <c>AddDbContextWith…</c> integration registers one; plain
    /// <c>AddDbContext</c> registrations are picked up by the implicit
    /// discovery hooked into <c>UseEntityFrameworkCoreTransactions</c>) and
    /// asks each one for a <see cref="DbContextUsage"/> snapshot. Sources
    /// that return null (transient configuration / DI failure) are silently
    /// skipped — same permissive policy as the document-store path.
    /// </summary>
    private static async Task readDbContexts(IWolverineRuntime runtime, CancellationToken token,
        ServiceCapabilities capabilities)
    {
        var sources = runtime.Services.GetServices<IDbContextUsageSource>();
        var seen = new HashSet<Uri>();
        var usageList = new List<DbContextUsage>();
        foreach (var source in sources)
        {
            // Dedupe by Subject URI — multiple registrations of the same
            // DbContext type (e.g. integration test harness re-registering)
            // shouldn't double-count.
            if (!seen.Add(source.Subject)) continue;

            var usage = await source.TryCreateUsage(token);
            if (usage != null)
            {
                usageList.Add(usage);
            }
        }

        capabilities.DbContexts.AddRange(usageList.OrderBy(x => x.SubjectUri.ToString()));
    }

    private static async Task readMessageStores(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var collection = runtime.Stores;
        var stores = await collection.FindAllAsync();
        capabilities.MessageStores.AddRange(stores.Select(MessageStore.For).OrderBy(x => x.Uri.ToString()));

        capabilities.MessageStoreCardinality = collection.Cardinality();
    }

    private static void readAdditionalCapabilities(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var descriptors = runtime.Services.GetServices<ICapabilityDescriptor>();
        foreach (var descriptor in descriptors)
        {
            capabilities.AdditionalCapabilities.Add(descriptor.Describe());
        }
    }

    private static void readTransports(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        foreach (var transport in runtime.Options.Transports)
        {
            if (transport.TryBuildBrokerUsage(out var usage))
            {
                capabilities.Brokers.Add(usage);
            }
        }
    }
}