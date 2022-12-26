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
}