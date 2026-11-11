using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// Phase-A counterpart to every ancillary-store routing attribute — <see cref="StorageAttribute"/> and each
/// integration's own spelling (<c>[MartenStore]</c>, <c>[PolecatStore]</c>, <c>[FisherStore]</c>), found
/// through <see cref="IAncillaryStoreAttribute"/>. Pre-populates <see cref="IChain.AncillaryStoreType"/> on
/// every handler chain decorated with one of them so the message-type-to-ancillary-store map that
/// WolverineRuntime.HostService builds eagerly during startup sees the targeting.
///
/// <para>
/// GH-2944. The routing attributes are <see cref="Wolverine.Attributes.ModifyChainAttribute"/>s, whose
/// <c>Modify()</c> runs lazily inside <c>HandlerChain.applyCustomizations</c> — at first-codegen time, when a
/// message of that type arrives or the chain is otherwise compiled. The HostService ancillary-store mapping
/// loop (<c>Handlers.AllChains().Where(c =&gt; c.AncillaryStoreType != null)</c>) runs eagerly during
/// <c>startMessagingTransportsAsync</c>, long before any chain is compiled, so without this policy that loop
/// would see a null <see cref="IChain.AncillaryStoreType"/> and a message arriving from an external system in
/// interop mode (no Wolverine headers) would land in the main store's inbox instead of the ancillary store's,
/// because DurableLocalQueue / DurableReceiver consult that map at receive time.
/// </para>
///
/// <para>
/// This is an <see cref="IHandlerPolicy"/>, which runs in Phase A (eager, at <c>HandlerGraph.Compile</c>), and
/// assigns just the <see cref="IChain.AncillaryStoreType"/> field. The Phase-B <c>Modify()</c> of the attribute
/// still runs later and re-assigns the same value (idempotent) plus inserts the provider's outbox-factory frame,
/// which has to stay lazy because middleware insertion participates in codegen. GH-5141: this one policy replaced
/// the three per-integration copies (<c>MartenStoreEagerPolicy</c>, <c>PolecatStoreEagerPolicy</c>,
/// <c>FisherStoreEagerPolicy</c>) that differed only in the attribute type they looked for.
/// </para>
///
/// <para>
/// Mirrors the discovery rules in <c>Chain.applyAttributesAndConfigureMethods</c> (handler method first, then
/// handler type — not the message type). Walks the per-endpoint sticky child chains
/// (<see cref="HandlerChain.ByEndpoint"/>) too, so <c>MultipleHandlerBehavior.Separated</c> keeps working —
/// matching the <c>AllChains()</c> iteration that the HostService loop uses (refs GH-2576).
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
            // GetCustomAttribute<T> cannot take an interface, hence the OfType<> over the full list
            var att = call.Method.GetCustomAttributes(inherit: true).OfType<IAncillaryStoreAttribute>().FirstOrDefault()
                      ?? call.HandlerType.GetCustomAttributes(inherit: true).OfType<IAncillaryStoreAttribute>().FirstOrDefault();

            if (att != null)
            {
                chain.AncillaryStoreType = att.StoreType;
                return;
            }
        }
    }
}
