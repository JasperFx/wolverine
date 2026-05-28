using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Marten;

/// <summary>
/// GH-2944: pre-populate <see cref="IChain.AncillaryStoreType"/> on every handler chain decorated
/// with <see cref="MartenStoreAttribute"/> so the message-type-to-ancillary-store map that
/// WolverineRuntime.HostService builds during startMessagingTransportsAsync sees the targeting.
///
/// MartenStoreAttribute is a ModifyChainAttribute, which means its Modify() runs LAZILY inside
/// HandlerChain.applyCustomizations - i.e. at first-codegen time, when a message of that type
/// arrives or the chain is otherwise compiled. WolverineRuntime.HostService runs the
/// ancillary-store mapping loop ('Handlers.AllChains().Where(c =&gt; c.AncillaryStoreType != null)')
/// EAGERLY during startMessagingTransportsAsync, long before any chain has had its
/// applyCustomizations triggered - so the loop sees a null AncillaryStoreType on every chain and
/// the map ends up empty. The downstream effect: a message arriving from an external system in
/// interop mode (no Wolverine headers) lands in the MAIN store's inbox instead of the ancillary
/// store's inbox, because DurableLocalQueue / DurableReceiver consult that map at receive time.
///
/// This policy is an IHandlerPolicy - which runs in Phase A (eager, at HandlerGraph.Compile) -
/// and assigns just the AncillaryStoreType field. The Phase B MartenStoreAttribute.Modify still
/// runs later and re-assigns the same value (idempotent) plus inserts AncillaryOutboxFactoryFrame,
/// which has to stay lazy because middleware insertion participates in codegen.
///
/// Mirrors the discovery rules in Chain.applyAttributesAndConfigureMethods (handler-type and
/// handler-method, not message-type). Also walks per-endpoint sticky child chains (ByEndpoint) so
/// MultipleHandlerBehavior.Separated keeps working - matches the AllChains() iteration that the
/// HostService loop already uses for the same reason (refs GH-2576).
/// </summary>
internal class MartenStoreEagerPolicy : IHandlerPolicy
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
            var att = call.Method.GetCustomAttribute<MartenStoreAttribute>(inherit: true)
                      ?? call.HandlerType.GetCustomAttribute<MartenStoreAttribute>(inherit: true);

            if (att != null)
            {
                chain.AncillaryStoreType = att.StoreType;
                return;
            }
        }
    }
}
