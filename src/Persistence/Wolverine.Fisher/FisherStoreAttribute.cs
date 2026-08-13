using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Fisher;

/// <summary>
/// Route a handler (or every handler on a class) to an ancillary (secondary) Fisher document store.
/// The Fisher mirror of <c>[MartenStore]</c>; the handler opens and commits its work through the
/// targeted store's outbox-enrolled session, and inline-projection side effects relayed by that store
/// flow through the Wolverine outbox. Use <c>[Storage(typeof(IMyStore))]</c> instead when you want a
/// single provider-agnostic attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class FisherStoreAttribute : ModifyChainAttribute
{
    public Type StoreType { get; }

    public FisherStoreAttribute(Type storeType)
    {
        StoreType = storeType;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.UseFisherStore(StoreType);
    }
}
