using Wolverine.Configuration;

namespace Wolverine.Transports.Sending;

public interface ISendingAgent
{
    Uri Destination { get; }
    Uri? ReplyUri { get; set; }
    bool Latched { get; }

    bool IsDurable { get; }

    bool SupportsNativeScheduledSend { get; }

    Endpoint Endpoint { get; }

    DateTimeOffset LastMessageSentAt { get; }

    /// <summary>
    ///     Attempt to start sending this envelope
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask EnqueueOutgoingAsync(Envelope envelope);

    /// <summary>
    ///     Without any external outbox, store and forward this envelope
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask StoreAndForwardAsync(Envelope envelope);

    /// <summary>
    ///     Persist this envelope in this agent's durable outbox <b>without</b> sending it, so that a caller
    ///     can settle other durable state before the send happens. Answers <c>false</c>, having done
    ///     nothing, for an agent with no outbox behind it.
    /// </summary>
    /// <remarks>
    ///     GH-4824. <see cref="StoreAndForwardAsync" /> cannot serve this: it stores and sends in one call,
    ///     and the point here is to get a durable home for the envelope while something else is still
    ///     true. The caller is <c>WolverineRuntime.EnqueueDirectlyAsync</c>, which has to delete a
    ///     forwarded envelope's INBOX row before the send puts a row on the destination queue — otherwise
    ///     the owning node's anti-duplicate probe (GH-4316) sees both and deletes the queue row, and the
    ///     message is neither handled nor dead lettered.
    ///
    ///     <para>A default implementation rather than a required member, so an external
    ///     <see cref="ISendingAgent" /> keeps compiling and keeps today's behaviour.</para>
    /// </remarks>
    ValueTask<bool> TryStoreOutgoingAsync(Envelope envelope) => new(false);
}