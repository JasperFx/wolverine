using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// Routes every message handler, HTTP endpoint and gRPC service in one assembly to an ancillary
/// (secondary) store, so a modular monolith's module does not have to repeat
/// <c>[Storage(typeof(IMyStore))]</c> on each of its types. GH-4477.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="IChainPolicy" /> rather than an <see cref="IHandlerPolicy" /> deliberately, because
/// it is the only policy kind all three graphs apply: <c>HandlerGraph</c> adapts it through
/// <c>HandlerChainPolicy</c>, and <c>HttpGraph</c> and <c>GrpcGraph</c> each pull
/// <c>IChainPolicy</c> straight off <c>WolverineOptions.Policies</c>. That also makes this the only
/// route to an ancillary store from a gRPC service at all -- the gRPC chains never run
/// <c>applyAttributesAndConfigureMethods</c>, so <c>[Storage]</c> on a gRPC service silently does
/// nothing.
/// </para>
/// <para>
/// Running as a policy also lands in the right phase for handler chains. Policies run at
/// <c>HandlerGraph.Compile</c>, which is before <c>WolverineRuntime.HostService</c> reads
/// <see cref="IChain.AncillaryStoreType" /> to build the message-type-to-store inbox routing map;
/// a <c>ModifyChainAttribute</c> alone is applied lazily at codegen and would be too late for that
/// map, which is what <c>StorageAttributeEagerPolicy</c> exists to work around.
/// </para>
/// </remarks>
internal class AncillaryStorageByAssemblyPolicy : IChainPolicy
{
    public AncillaryStorageByAssemblyPolicy(Type storeType, Assembly assembly)
    {
        StoreType = storeType ?? throw new ArgumentNullException(nameof(storeType));
        Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }

    public Type StoreType { get; }
    public Assembly Assembly { get; }

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // An explicit [Storage]/[MartenStore]/[PolecatStore]/[FisherStore] on the type or method
            // wins over the assembly-wide default. Skipping is also what keeps the chain correct:
            // the attribute's Modify() runs later, at codegen, and would insert a SECOND outbox
            // factory frame on top of the one this policy inserted.
            if (chain.DetermineAncillaryStoreType() != null) continue;

            if (!isFromAssembly(chain)) continue;

            chain.UseAncillaryStorage(StoreType, container);
        }
    }

    private bool isFromAssembly(IChain chain)
    {
        // Handler and HTTP chains: the handler methods and the endpoint method respectively.
        foreach (var call in chain.HandlerCalls())
        {
            if (call.HandlerType?.Assembly == Assembly) return true;
        }

        // gRPC chains report an empty HandlerCalls(), so they answer through IChainSourceType.
        if (chain is IChainSourceType sourced && sourced.SourceType.Assembly == Assembly) return true;

        return false;
    }
}
