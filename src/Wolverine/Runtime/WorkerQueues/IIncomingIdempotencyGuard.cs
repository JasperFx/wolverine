using JasperFx.Core;

namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// GH-3710. Opt-in, per-process, bounded duplicate detection for endpoints that have no durable inbox.
///
/// <para>
/// The durable inbox deduplicates by the primary key of <c>wolverine_incoming</c>, which is why a
/// <see cref="Configuration.EndpointMode.Durable"/> endpoint never needs this. Every other mode --
/// <see cref="Configuration.EndpointMode.NativeAck"/> above all, which deliberately leaves deliveries
/// unacknowledged and therefore *expects* redelivery on any rolling deploy -- has nothing at all. This is
/// the in-memory analogue: recognisably the same feature as
/// <c>DurableReceiver.handleDuplicateIncomingEnvelope</c> (ack the duplicate delivery, drop it, do not
/// execute it) minus the database.
/// </para>
///
/// <para>
/// The honest limit, which the docs state plainly: this is per-process and in memory. A restart forgets
/// everything, and a second node (or a slot failover to one) starts empty. The promise is
/// "at-least-once with best-effort dedup", not exactly-once. Anyone needing hard dedup keeps the durable
/// inbox, or leans on a broker that has its own dedup window (NATS JetStream's <c>Nats-Msg-Id</c>, SQS
/// FIFO's <c>MessageDeduplicationId</c>, Pub/Sub's <c>deduplication-id</c>).
/// </para>
/// </summary>
internal interface IIncomingIdempotencyGuard
{
    /// <summary>
    /// Claim this envelope for processing. Returns <c>false</c> when the id is already in flight or was
    /// already processed within the window, in which case the caller settles the delivery and drops it.
    /// </summary>
    bool TryBeginProcessing(Envelope envelope);

    /// <summary>
    /// Move an id from in-flight to processed. Call this only when the delivery reached a terminal the
    /// broker will not redeliver.
    /// </summary>
    void MarkProcessed(Envelope envelope);

    /// <summary>
    /// Drop an id from the in-flight set WITHOUT marking it processed. This is the failure path: a
    /// nacked or requeued delivery is coming back, and suppressing it would turn a retry into a message
    /// loss.
    /// </summary>
    void Release(Envelope envelope);
}

/// <summary>
/// GH-3710. Identity of a received delivery for duplicate detection purposes, honoring
/// <see cref="DurabilitySettings.MessageIdentity"/>: under
/// <see cref="Wolverine.MessageIdentity.IdAndDestination"/> the same message id arriving at two different
/// listening endpoints of one process (the Modular Monolith shape) is two distinct messages, not a duplicate.
/// </summary>
internal readonly record struct IdempotencyKey(Guid Id, Uri? Destination)
{
    public static IdempotencyKey For(Envelope envelope, MessageIdentity identity)
    {
        return identity == MessageIdentity.IdAndDestination
            ? new IdempotencyKey(envelope.Id, envelope.Destination)
            : new IdempotencyKey(envelope.Id, null);
    }
}

/// <summary>
/// GH-3710. Tuning for the opt-in in-memory idempotency guard on a non-durable listening endpoint.
/// </summary>
public class InMemoryIdempotencySettings
{
    /// <summary>
    /// The default duplicate-suppression window, 5 minutes. Comfortably longer than the redelivery burst
    /// a rolling deploy produces on a <see cref="Configuration.EndpointMode.NativeAck"/> endpoint, which is
    /// bounded by the broker's prefetch depth and settles within seconds.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = 5.Minutes();

    /// <summary>
    /// The default ceiling on tracked message ids, 100,000. At 16 bytes of Guid plus hash set overhead this
    /// is single-digit megabytes, and it is a hard ceiling rather than a target: a sustained flood of unique
    /// ids evicts by size long before the window elapses.
    /// </summary>
    public const int DefaultMaxTracked = 100_000;

    private TimeSpan _window = DefaultWindow;
    private int _maxTracked = DefaultMaxTracked;

    /// <summary>
    /// How long a completed message id is remembered. Eviction is generational rather than per-entry, so
    /// an id is remembered for at least half this window and at most all of it.
    /// </summary>
    public TimeSpan Window
    {
        get => _window;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(Window),
                    "The in-memory idempotency window must be greater than zero");
            }

            _window = value;
        }
    }

    /// <summary>
    /// The maximum number of completed message ids held in memory. Reaching this rotates the generations
    /// early, so memory stays bounded under a sustained flood of unique ids at the cost of a shorter
    /// effective window.
    /// </summary>
    public int MaxTracked
    {
        get => _maxTracked;
        set
        {
            if (value < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxTracked),
                    "The in-memory idempotency guard must be allowed to track at least 2 message ids");
            }

            _maxTracked = value;
        }
    }

    public override string ToString()
    {
        return $"{nameof(Window)}: {Window}, {nameof(MaxTracked)}: {MaxTracked}";
    }
}
