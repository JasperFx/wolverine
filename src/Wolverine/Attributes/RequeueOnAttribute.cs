using System;
using JasperFx.CodeGeneration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
///     Applies an error handling polity to requeue a message if it
///     encounters an exception of the designated type
/// </summary>
public class RequeueOnAttribute : ModifyHandlerChainAttribute
{
    private readonly int _attempts;
    private readonly Type _exceptionType;

    public RequeueOnAttribute(Type exceptionType, int attempts = 3)
    {
        _exceptionType = exceptionType;
        _attempts = attempts;
    }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.OnExceptionOfType(_exceptionType)
            .Requeue(_attempts);
    }
}