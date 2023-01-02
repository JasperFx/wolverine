using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

public interface ITransactionFrameProvider
{
    void ApplyTransactionSupport(IChain chain, IContainer container);
    bool CanApply(IChain chain, IContainer container);
}

internal class AutoApplyTransactions : IHandlerPolicy
{
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        throw new System.NotImplementedException();
    }
}