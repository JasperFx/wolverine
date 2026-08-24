using System.Collections.Concurrent;
using Google.Cloud.PubSub.V1;

namespace Wolverine.Pubsub.Internal;

/// <summary>
/// GH-4052. One Pub/Sub delivery whose subscriber callback is being <b>held open</b> until every envelope
/// it carried reaches a terminal.
/// </summary>
/// <remarks>
/// <para>Pub/Sub has no per-message settle API — <c>PubsubListener.CompleteAsync</c> is a no-op, and always
/// was. Acknowledgement happens entirely through the value the subscriber callback returns, so the only way
/// to not settle on receipt is to not return yet. The spike (GH-4052) measured that
/// <c>SubscriberClient</c> dispatches callbacks concurrently while others are held — 1000 of 1200 held
/// simultaneously — so holding is a live design rather than a serialisation trap.</para>
///
/// <para>Keyed by the <b>delivery</b>, not the envelope: one Pub/Sub message can carry many envelopes when
/// Wolverine batched them on the send side, and a single Ack/Nack settles the lot. So the reply is decided
/// by counting envelopes down, and any single failure makes the whole delivery a Nack — the batch is
/// redelivered and the durable inbox or the in-memory idempotency guard deduplicates the ones that already
/// succeeded.</para>
/// </remarks>
internal sealed class PubsubHeldDelivery
{
    private readonly TaskCompletionSource<SubscriberClient.Reply> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _outstanding;
    private int _failed;

    public PubsubHeldDelivery(string key, int envelopeCount)
    {
        Key = key;
        _outstanding = envelopeCount;
    }

    public string Key { get; }

    public Task<SubscriberClient.Reply> Reply => _completion.Task;

    /// <summary>One envelope reached a successful terminal. Settles the delivery when it was the last.</summary>
    public bool Succeeded()
    {
        return countDown();
    }

    /// <summary>
    /// One envelope reached a failing terminal. The delivery is doomed to Nack, but still waits for its
    /// siblings so a batch is not settled while some of it is still running.
    /// </summary>
    public bool Failed()
    {
        Interlocked.Exchange(ref _failed, 1);
        return countDown();
    }

    /// <summary>
    /// Settle immediately regardless of outstanding envelopes. Used on shutdown, where the alternative is
    /// a callback that never returns and a <c>StopAsync</c> that never completes (spike section 4c).
    /// </summary>
    public bool NackNow()
    {
        return _completion.TrySetResult(SubscriberClient.Reply.Nack);
    }

    private bool countDown()
    {
        if (Interlocked.Decrement(ref _outstanding) > 0) return false;

        return _completion.TrySetResult(Volatile.Read(ref _failed) == 1
            ? SubscriberClient.Reply.Nack
            : SubscriberClient.Reply.Ack);
    }
}

/// <summary>
/// GH-4052. The deliveries a listener is currently holding, and the envelope-to-delivery lookup that lets a
/// settle call find its delivery.
/// </summary>
internal sealed class PubsubHeldDeliveries
{
    public const string DeliveryKeyHeader = "wolverine-pubsub-delivery";

    private readonly ConcurrentDictionary<string, PubsubHeldDelivery> _held = new();

    public PubsubHeldDelivery Hold(string key, int envelopeCount)
    {
        var delivery = new PubsubHeldDelivery(key, envelopeCount);
        _held[key] = delivery;
        return delivery;
    }

    public void Release(string key) => _held.TryRemove(key, out _);

    public bool TryFind(Envelope envelope, out PubsubHeldDelivery delivery)
    {
        delivery = null!;

        if (!envelope.Headers.TryGetValue(DeliveryKeyHeader, out var key) || key is null) return false;

        return _held.TryGetValue(key, out delivery!);
    }

    /// <summary>Nack everything still held. Idempotent, and safe to call from a cancellation callback.</summary>
    public void NackAll()
    {
        foreach (var delivery in _held.Values)
        {
            delivery.NackNow();
        }
    }
}
