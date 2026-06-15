using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// Phase-A counterpart to <see cref="StorageAttribute"/>. Pre-populates
/// <see cref="IChain.AncillaryStoreType"/> on every handler chain decorated with
/// <c>[Storage(typeof(...))]</c> so the message-type-to-ancillary-store map that
/// WolverineRuntime.HostService builds eagerly during startup sees the targeting.
///
/// <para>
/// <see cref="StorageAttribute"/> is a <see cref="Wolverine.Attributes.ModifyChainAttribute"/>, whose
/// <c>Modify()</c> runs lazily at first-codegen time. The HostService ancillary-store mapping loop
/// (<c>Handlers.AllChains().Where(c =&gt; c.AncillaryStoreType != null)</c>) runs eagerly, long before
/// any chain is compiled, so without this policy that loop would see a null
/// <see cref="IChain.AncillaryStoreType"/> and external interop messages would land in the main store's
/// inbox instead of the ancillary store's. This mirrors the per-provider eager policies (Marten's
/// <c>MartenStoreEagerPolicy</c>); the Phase-B <c>StorageAttribute.Modify</c> still runs later and
/// re-assigns the same value (idempotent) plus inserts the provider's outbox-factory frame.
/// </para>
///
/// <para>
/// Walks the per-endpoint sticky child chains (<see cref="HandlerChain.ByEndpoint"/>) too, so
/// <c>MultipleHandlerBehavior.Separated</c> keeps working — matching the <c>AllChains()</c> iteration
/// that the HostService loop uses (refs GH-2576/GH-2944).
/// </para>
/// </summary>
internal class StorageAttributeEagerPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            applyTo(chain);

            foreach (var byEndpoint in chain.ByEndpoint)
            {
                applyTo(byEndpoint);
            }
        }
    }

    private static void applyTo(HandlerChain chain)
    {
        if (chain.AncillaryStoreType != null) return;

        foreach (var call in chain.Handlers)
        {
            var att = call.Method.GetCustomAttribute<StorageAttribute>(inherit: true)
                      ?? call.HandlerType.GetCustomAttribute<StorageAttribute>(inherit: true);

            if (att != null)
            {
                chain.AncillaryStoreType = att.StoreType;
                return;
            }
        }
    }
}
