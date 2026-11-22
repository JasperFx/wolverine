namespace Wolverine.Pubsub.Internal;

/// <summary>
/// GH-5771. One Pub/Sub delivery to an Inline endpoint while its subscriber callback is still running the handlers.
/// Outside NativeAck a requeue publishes a copy, and the callback's reply settles the original, so the callback
/// may only Ack once every requeue it saw actually published its copy.
/// </summary>
internal sealed class PubsubInlineDelivery
{
    private int _failedRequeue;

    public bool HasFailedRequeue => Volatile.Read(ref _failedRequeue) == 1;

    /// <summary>A requeue copy could not be published, so the original has to be nacked and redelivered.</summary>
    public void RequeueFailed() => Interlocked.Exchange(ref _failedRequeue, 1);
}
