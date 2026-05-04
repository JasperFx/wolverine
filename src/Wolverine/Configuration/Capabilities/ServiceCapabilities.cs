using System.Reflection;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
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

    public List<MessageDescriptor> Messages { get; set; } = [];

    /// <summary>
    /// One descriptor per concrete <see cref="Saga"/> state class
    /// discovered in the handler graph. Each descriptor lists the
    /// messages that touch the saga, the role each message plays
    /// (Start / StartOrHandle / Orchestrate / NotFound), and the
    /// cascading messages each handler emits. Consumed by external
    /// monitoring tools (CritterWatch) to render saga workflow
    /// diagrams without having to introspect runtime types.
    /// </summary>
    public List<SagaDescriptor> Sagas { get; set; } = [];

    public List<MessageStore> MessageStores { get; set; } = [];

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

        readMessageTypes(runtime, capabilities);

        readEndpoints(runtime, capabilities);

        readSagas(runtime, capabilities);

        readAdditionalCapabilities(runtime, capabilities);

        return capabilities;
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
    /// Walk every <see cref="SagaChain"/> on the handler graph (including
    /// per-endpoint variants in MultipleHandlerBehavior.Separated mode) and
    /// emit one <see cref="SagaDescriptor"/> per concrete saga state type.
    /// Each handler call inside the chain contributes a
    /// <see cref="SagaMessageRole"/> classified by the same method-name
    /// matching that <see cref="SagaChain.DetermineFrames"/> uses for
    /// code-gen, so what the descriptor reports is exactly what Wolverine
    /// will execute at runtime.
    /// </summary>
    private static void readSagas(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var sagaChains = collectSagaChains(runtime.Options.HandlerGraph).ToArray();
        if (sagaChains.Length == 0) return;

        // (sagaStateType, messageType) is unique within a saga — a single
        // chain handles one message type, classified by its handler method
        // name. We group by saga state type to produce one descriptor per
        // saga, and within each group preserve the (chain → role) mapping.
        var groups = sagaChains
            .Where(c => c.Handlers.Any(h => h.HandlerType.CanBeCastTo<Saga>()))
            .GroupBy(c => c.SagaType);

        foreach (var group in groups.OrderBy(g => g.Key.FullNameInCode()))
        {
            var stateType = group.Key;
            var descriptor = new SagaDescriptor(TypeDescriptor.For(stateType));

            // SagaIdType is consistent across every chain for a single saga
            // (Wolverine would error at runtime if it weren't), so pull it
            // from whichever chain first resolved a SagaIdMember. The id
            // member NAME varies per message, hence is captured per-role.
            var typeSource = group.FirstOrDefault(c => c.SagaIdMember != null);
            if (typeSource is not null)
            {
                descriptor.SagaIdType = sagaIdMemberType(typeSource.SagaIdMember!)?.FullName;
            }

            foreach (var chain in group.OrderBy(c => c.MessageType.FullNameInCode()))
            {
                var role = classifySagaChainRole(chain);
                if (role is null) continue;

                var published = chain.PublishedTypes()
                    .Distinct()
                    .Select(TypeDescriptor.For)
                    .ToArray();

                descriptor.Messages.Add(new SagaMessageRole(
                    TypeDescriptor.For(chain.MessageType),
                    role.Value,
                    chain.SagaIdMember?.Name,
                    published));
            }

            capabilities.Sagas.Add(descriptor);
        }
    }

    /// <summary>
    /// Classify a single SagaChain into the role that best summarises the
    /// chain's handler method names. A chain may contain multiple methods
    /// for the same message (e.g. both <c>StartOrHandle</c> and a separate
    /// <c>NotFound</c>) — in that case <c>StartOrHandle</c> wins because
    /// it's the strictly-more-capable role. Returns null when the chain
    /// has no recognisable saga-handler methods (shouldn't happen in
    /// practice, but defensive).
    /// </summary>
    private static SagaRole? classifySagaChainRole(SagaChain chain)
    {
        var methodNames = chain.Handlers
            .Where(h => h.HandlerType.CanBeCastTo<Saga>())
            .Select(h => h.Method.Name)
            .Select(n => n.EndsWith("Async") ? n[..^"Async".Length] : n)
            .ToHashSet();

        if (methodNames.Contains(SagaChain.StartOrHandle) || methodNames.Contains(SagaChain.StartsOrHandles))
            return SagaRole.StartOrHandle;

        if (methodNames.Contains(SagaChain.Start) || methodNames.Contains(SagaChain.Starts))
            return SagaRole.Start;

        if (methodNames.Contains(SagaChain.Orchestrate) || methodNames.Contains(SagaChain.Orchestrates)
            || methodNames.Contains("Handle") || methodNames.Contains("Handles")
            || methodNames.Contains("Consume") || methodNames.Contains("Consumes"))
            return SagaRole.Orchestrate;

        if (methodNames.Contains(SagaChain.NotFound))
            return SagaRole.NotFound;

        return null;
    }

    /// <summary>
    /// Recursively yield every SagaChain reachable through the handler
    /// graph, including per-endpoint variants created by
    /// MultipleHandlerBehavior.Separated. Top-level chains may have moved
    /// their handlers into ByEndpoint sub-chains, leaving the outer chain
    /// "routing only" — we still want the inner chains' roles.
    /// </summary>
    private static IEnumerable<SagaChain> collectSagaChains(HandlerGraph graph)
    {
        foreach (var chain in graph.Chains.OfType<SagaChain>())
        {
            yield return chain;
            foreach (var inner in chain.ByEndpoint.OfType<SagaChain>())
            {
                yield return inner;
            }
        }
    }

    private static Type? sagaIdMemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => null
    };

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