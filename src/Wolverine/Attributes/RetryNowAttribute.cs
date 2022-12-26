using JasperFx.CodeGeneration;
using JasperFx.Core;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
///     Applies an error policy that a message should be retried
///     whenever processing encounters the designated exception type
///     with a specified number of cooldown periods before being moved to a dead letter queue
/// </summary>
public class RetryNowAttribute : ModifyHandlerChainAttribute
{
    private readonly int[] _cooldownTimeInMilliseconds;
    private readonly Type _exceptionType;

    public RetryNowAttribute(Type exceptionType, params int[] cooldownTimeInMilliseconds)
    {
        _exceptionType = exceptionType;
        _cooldownTimeInMilliseconds = cooldownTimeInMilliseconds;
    }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.OnExceptionOfType(_exceptionType)
            .RetryWithCooldown(_cooldownTimeInMilliseconds.Select(x => x.Milliseconds()).ToArray());
    }
}