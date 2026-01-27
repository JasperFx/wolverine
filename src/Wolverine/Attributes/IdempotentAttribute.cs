using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
/// Adds idempotency checks to this message handler
///
/// ONLY use this for message handlers that do not use transactional
/// middleware
/// </summary>
public class IdempotentAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.ApplyIdempotencyCheck();
    }
}