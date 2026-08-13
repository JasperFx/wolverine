using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Fisher;

/// <summary>
/// Phase-A counterpart to <see cref="FisherStoreAttribute"/>. Pre-populates
/// <see cref="IChain.AncillaryStoreType"/> on every handler chain decorated with
/// <see cref="FisherStoreAttribute"/> so the message-type-to-ancillary-store map that
/// WolverineRuntime.HostService builds eagerly during startup sees the targeting. This is the Fisher
/// mirror of Marten's <c>MartenStoreEagerPolicy</c> — see that type for the Phase-A vs Phase-B ordering
/// trap (GH-2944) this addresses. The Phase-B <see cref="FisherStoreAttribute.Modify"/> still runs later
/// and re-assigns the same value (idempotent) plus inserts the Fisher outbox-factory frame.
///
/// <para>
/// Walks the per-endpoint sticky child chains (<see cref="HandlerChain.ByEndpoint"/>) too so
/// <c>MultipleHandlerBehavior.Separated</c> keeps working — matching the <c>AllChains()</c> iteration the
/// HostService loop uses (refs GH-2576).
/// </para>
/// </summary>
internal class FisherStoreEagerPolicy : IHandlerPolicy
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
            var att = call.Method.GetCustomAttribute<FisherStoreAttribute>(inherit: true)
                      ?? call.HandlerType.GetCustomAttribute<FisherStoreAttribute>(inherit: true);

            if (att != null)
            {
                chain.AncillaryStoreType = att.StoreType;
                return;
            }
        }
    }
}
