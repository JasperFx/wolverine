using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Polecat;

/// <summary>
/// Phase-A counterpart to <see cref="PolecatStoreAttribute"/>. Pre-populates
/// <see cref="IChain.AncillaryStoreType"/> on every handler chain decorated with
/// <see cref="PolecatStoreAttribute"/> so the message-type-to-ancillary-store map that
/// WolverineRuntime.HostService builds eagerly during startup sees the targeting. This is the Polecat
/// mirror of Marten's <c>MartenStoreEagerPolicy</c> — see that type for the Phase-A vs Phase-B ordering
/// trap (GH-2944) this addresses. The Phase-B <see cref="PolecatStoreAttribute.Modify"/> still runs later
/// and re-assigns the same value (idempotent) plus inserts the Polecat outbox-factory frame.
///
/// <para>
/// Walks the per-endpoint sticky child chains (<see cref="HandlerChain.ByEndpoint"/>) too so
/// <c>MultipleHandlerBehavior.Separated</c> keeps working — matching the <c>AllChains()</c> iteration the
/// HostService loop uses (refs GH-2576).
/// </para>
/// </summary>
internal class PolecatStoreEagerPolicy : IHandlerPolicy
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
            var att = call.Method.GetCustomAttribute<PolecatStoreAttribute>(inherit: true)
                      ?? call.HandlerType.GetCustomAttribute<PolecatStoreAttribute>(inherit: true);

            if (att != null)
            {
                chain.AncillaryStoreType = att.StoreType;
                return;
            }
        }
    }
}
