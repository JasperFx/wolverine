namespace Wolverine.Pubsub.Internal;

/// <summary>
/// One Pub/Sub delivery to an Inline endpoint while its subscriber callback is still running the handlers. Outside
/// NativeAck a requeue (GH-5771) or a move to the native dead-letter topic (GH-5802) publishes a copy, and the
/// callback's reply settles the original, so the callback may only Ack once every copy it was asked for was
/// actually published.
/// </summary>
internal sealed class PubsubInlineDelivery
{
    private int _copyNotPublished;

    public bool MustNack => Volatile.Read(ref _copyNotPublished) == 1;

    /// <summary>A copy could not be published, so the original has to be nacked and kept by Pub/Sub.</summary>
    public void CopyNotPublished() => Interlocked.Exchange(ref _copyNotPublished, 1);
}
