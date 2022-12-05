using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

public class MessageTimeoutAttribute : ModifyHandlerChainAttribute
{
    public MessageTimeoutAttribute(int timeoutInSeconds)
    {
        TimeoutInSeconds = timeoutInSeconds;
    }

    public int TimeoutInSeconds { get; }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.ExecutionTimeoutInSeconds = TimeoutInSeconds;
    }
}