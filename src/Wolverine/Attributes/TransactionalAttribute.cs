using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Attributes;

/// <summary>
///     Applies unit of work / transactional boundary middleware to the
///     current chain using the currently configured persistence
/// </summary>
public class TransactionalAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IContainer container)
    {
        var transactionFrameProvider = rules.As<GenerationRules>().GetTransactions(chain, container);
        transactionFrameProvider.ApplyTransactionSupport(chain, container);
    }
}