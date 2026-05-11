using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Shared helper that materialises a <see cref="SagaDescriptor"/> from the
/// handler graph + saga state type. Used by
/// <see cref="ServiceCapabilities.ReadFrom"/> when emitting host-wide
/// capabilities and by per-storage <see cref="ISagaStoreDiagnostics"/>
/// implementations when reporting the saga types they own. Single source
/// of truth so the host-wide snapshot and the per-storage view agree
/// byte-for-byte on classification.
/// </summary>
internal static class SagaDescriptorBuilder
{
    /// <summary>
    /// Build one <see cref="SagaDescriptor"/> for a single saga state
    /// type by walking every <see cref="SagaChain"/> on the graph that
    /// targets this type, classifying each chain by its handler method
    /// names, and capturing per-message saga-id binding + cascading
    /// PublishedTypes.
    /// </summary>
    /// <param name="graph">The Wolverine handler graph.</param>
    /// <param name="sagaType">Concrete <c>: Saga</c> state class.</param>
    /// <param name="storageProvider">
    /// Optional storage-provider tag (e.g. <c>"Marten"</c>); null when
    /// the caller doesn't know.
    /// </param>
    public static SagaDescriptor Build(HandlerGraph graph, Type sagaType, string? storageProvider)
    {
        var descriptor = new SagaDescriptor(TypeDescriptor.For(sagaType))
        {
            StorageProvider = storageProvider
        };

        var chainsForSaga = CollectSagaChains(graph)
            .Where(c => c.SagaType == sagaType
                        && c.Handlers.Any(h => h.HandlerType.CanBeCastTo<Saga>()))
            .OrderBy(c => c.MessageType.FullNameInCode())
            .ToArray();

        // SagaIdType is consistent across every chain for one saga (Wolverine
        // would have errored at runtime if it weren't), so pull it from the
        // first chain that resolved a SagaIdMember.
        var typeSource = chainsForSaga.FirstOrDefault(c => c.SagaIdMember != null);
        if (typeSource is not null)
        {
            descriptor.SagaIdType = SagaIdMemberType(typeSource.SagaIdMember!)?.FullName;
        }

        foreach (var chain in chainsForSaga)
        {
            var role = ClassifyRole(chain);
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

        return descriptor;
    }

    /// <summary>
    /// Classify a single <see cref="SagaChain"/> into the role that best
    /// summarises the chain's handler method names. Mirrors the lookup
    /// <see cref="SagaChain.DetermineFrames"/> uses for code-gen so the
    /// static descriptor matches runtime behaviour. <c>StartOrHandle</c>
    /// wins over <c>Start</c> because it's the strictly-more-capable role.
    /// Returns null when the chain has no recognisable saga-handler
    /// methods (shouldn't happen in practice, but defensive).
    /// </summary>
    public static SagaRole? ClassifyRole(SagaChain chain)
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
    /// Recursively yield every <see cref="SagaChain"/> reachable through
    /// the handler graph, including per-endpoint variants created by
    /// MultipleHandlerBehavior.Separated. Top-level chains may have moved
    /// their handlers into <see cref="MessageHandler.ByEndpoint"/>
    /// sub-chains, leaving the outer chain routing-only — we still want
    /// the inner chains' roles.
    /// </summary>
    public static IEnumerable<SagaChain> CollectSagaChains(HandlerGraph graph)
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

    /// <summary>
    /// Unwrap a saga-id MemberInfo to its declared type. Used to populate
    /// <see cref="SagaDescriptor.SagaIdType"/>.
    /// </summary>
    public static Type? SagaIdMemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => null
    };
}
