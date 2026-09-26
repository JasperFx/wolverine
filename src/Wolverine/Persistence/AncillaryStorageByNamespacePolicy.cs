using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// Routes every message handler, HTTP endpoint and gRPC service in one namespace (and its child
/// namespaces) to an ancillary store. The namespace counterpart of
/// <see cref="AncillaryStorageByAssemblyPolicy" />, for modules that share an assembly.
/// </summary>
internal class AncillaryStorageByNamespacePolicy : IChainPolicy
{
    public AncillaryStorageByNamespacePolicy(Type storeType, string @namespace)
    {
        StoreType = storeType ?? throw new ArgumentNullException(nameof(storeType));

        if (string.IsNullOrEmpty(@namespace))
        {
            throw new ArgumentOutOfRangeException(nameof(@namespace), "A namespace is required");
        }

        Namespace = @namespace;
    }

    public Type StoreType { get; }
    public string Namespace { get; }

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // Same precedence as AncillaryStorageByAssemblyPolicy: an explicit attribute wins
            if (chain.DetermineAncillaryStoreType() != null) continue;

            if (!isFromNamespace(chain)) continue;

            chain.UseAncillaryStorage(StoreType, container);
        }
    }

    private bool isFromNamespace(IChain chain)
    {
        foreach (var call in chain.HandlerCalls())
        {
            if (isInNamespace(call.HandlerType)) return true;
        }

        return chain is IChainSourceType sourced && isInNamespace(sourced.SourceType);
    }

    private bool isInNamespace(Type? type)
    {
        var ns = type?.Namespace;
        if (ns == null) return false;

        return ns == Namespace || ns.StartsWith(Namespace + ".", StringComparison.Ordinal);
    }
}
