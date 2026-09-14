using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Marten;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class MartenStoreAttribute : ModifyChainAttribute, IAncillaryStoreAttribute
{
    public Type StoreType { get; }

    public MartenStoreAttribute(Type storeType)
    {
        StoreType = storeType;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.UseMartenStore(StoreType);
    }
}
