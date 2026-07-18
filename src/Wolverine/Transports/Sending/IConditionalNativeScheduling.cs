namespace Wolverine.Transports.Sending;

/// <summary>
///     Optional, envelope-aware companion to <see cref="ISender.SupportsNativeScheduledSend" /> (and
///     <see cref="ISenderProtocolWithNativeScheduling" /> for batched senders) for transports whose
///     native scheduled delivery only covers some messages. Example: Amazon SQS caps per-message
///     <c>DelaySeconds</c> at 15 minutes and disallows it entirely on FIFO queues. When an
///     <see cref="ISender" /> or <see cref="ISenderProtocol" /> implements this interface, Wolverine
///     asks per envelope at routing time whether the transport can honor the requested delivery time
///     natively, and falls back to its own message scheduling (the durable message store, or the
///     in-memory scheduler when there is no message store) for envelopes that cannot be scheduled
///     natively
/// </summary>
public interface IConditionalNativeScheduling
{
    /// <summary>
    ///     Can the requested delivery time of this particular envelope be honored by the
    ///     transport's native scheduled delivery?
    /// </summary>
    bool CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow);
}

public static class ConditionalNativeSchedulingExtensions
{
    /// <summary>
    ///     Envelope-aware version of <see cref="ISendingAgent.SupportsNativeScheduledSend" />. Answers
    ///     whether this sending agent can natively schedule delivery of this particular envelope by
    ///     consulting <see cref="IConditionalNativeScheduling" /> on the underlying sender when the
    ///     transport's native scheduling support is conditional
    /// </summary>
    public static bool SupportsNativeScheduledSendFor(this ISendingAgent agent, Envelope envelope,
        DateTimeOffset utcNow)
    {
        if (!agent.SupportsNativeScheduledSend)
        {
            return false;
        }

        // Unwrap the agent to the actual transport sender where the conditional
        // capability lives. Local queues and other ISendingAgent implementations
        // that are not sender wrappers answer for themselves
        var sender = agent switch
        {
            SendingAgent sendingAgent => (object)sendingAgent.Sender,
            InlineSendingAgent inlineAgent => inlineAgent.Sender,
            _ => agent
        };

        return sender is not IConditionalNativeScheduling conditional ||
               conditional.CanScheduleNatively(envelope, utcNow);
    }
}
