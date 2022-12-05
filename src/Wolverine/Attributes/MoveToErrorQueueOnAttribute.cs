using System;
using JasperFx.CodeGeneration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
///     Move the message to the error queues on encountering the named Exception type
/// </summary>
public class MoveToErrorQueueOnAttribute : ModifyHandlerChainAttribute
{
    private readonly Type _exceptionType;

    public MoveToErrorQueueOnAttribute(Type exceptionType)
    {
        _exceptionType = exceptionType;
    }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.OnExceptionOfType(_exceptionType).MoveToErrorQueue();
    }
}