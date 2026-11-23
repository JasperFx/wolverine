using Wolverine.Persistence;

namespace Wolverine.Runtime;

/// <summary>
/// GH-5613. What a <c>[Deduplicated]</c> message handler's generated code calls to record how a message ended.
/// Public only because generated code calls it.
/// </summary>
public static class MessageDeduplicationOutcomes
{
    /// <param name="context">The context of the message being handled</param>
    /// <param name="outcome">How it ended</param>
    /// <param name="handlerType">The chain's handler type(s), as the <c>message.handler</c> span tag names them</param>
    public static void Record(MessageContext context, DeduplicatedExecutionOutcome outcome, string handlerType)
        => (context.Runtime as WolverineRuntime)?.RecordDeduplicationOutcome(context.Envelope, outcome, handlerType);
}
