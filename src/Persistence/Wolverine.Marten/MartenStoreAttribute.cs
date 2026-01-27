using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Runtime;

namespace Wolverine.Marten;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class MartenStoreAttribute : ModifyChainAttribute
{
    public Type StoreType { get; }

    public MartenStoreAttribute(Type storeType)
    {
        StoreType = storeType;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.Middleware.Insert(0, new AncillaryOutboxFactoryFrame(StoreType));
    }
}