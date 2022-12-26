using JasperFx.CodeGeneration;
using JasperFx.Core;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
///     Applies an error handling policy to schedule a message to be retried
///     in a designated number of seconds after encountering the named exception
/// </summary>
public class ScheduleRetryAttribute : ModifyHandlerChainAttribute
{
    private readonly Type _exceptionType;
    private readonly int[] _seconds;

    public ScheduleRetryAttribute(Type exceptionType, params int[] seconds)
    {
        _exceptionType = exceptionType;
        _seconds = seconds;
    }

    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        var timeSpans = _seconds.Select(x => x.Seconds()).ToArray();
        chain.OnExceptionOfType(_exceptionType)
            .ScheduleRetry(timeSpans);
    }
}