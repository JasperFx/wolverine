using Wolverine.Runtime;

namespace Wolverine.Transports;

/// <summary>
///     Marks an IChannelCallback as supporting a native dead letter queue
///     functionality
/// </summary>
public interface ISupportDeadLetterQueue
{
    bool NativeDeadLetterQueueEnabled { get; }
    Task MoveToErrorsAsync(Envelope envelope, Exception exception);
}

/// <summary>
///     Marks an IChannelCallback as supporting native scheduled send
/// </summary>
public interface ISupportNativeScheduling
{
    /// <summary>
    ///     Move the current message represented by the envelope to a
    ///     scheduled delivery
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="time"></param>
    /// <returns></returns>
    Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time);

    /// <summary>
    ///     Whether this implementation can actually honor a native reschedule for the current
    ///     configuration. Defaults to true. Implementations whose native scheduling is conditional
    ///     (e.g. the Pulsar listener, which only reschedules when a retry-letter topic is configured)
    ///     override this so the runtime falls back to the durable/buffered rescheduler instead of
    ///     silently no-op'ing. Mirrors <see cref="ISupportDeadLetterQueue.NativeDeadLetterQueueEnabled" />.
    /// </summary>
    bool NativeSchedulingEnabled => true;
}

/// <summary>
/// Marks a listener as supporting multiple consumers reading from the same stream or queue,
/// allowing the system to differentiate between multiple listeners with the same URI
/// </summary>
public interface ISupportMultipleConsumers
{
    /// <summary>
    /// Gets a unique identifier for this specific consumer instance
    /// </summary>
    string? ConsumerId { get; internal set; }

    /// <summary>
    /// Gets the base address without any consumer-specific information
    /// </summary>
    Uri BaseAddress { get; }

    /// <summary>
    /// Gets the consumer-specific address that can be used to uniquely identify this consumer instance
    /// when storing messages
    /// </summary>
    Uri ConsumerAddress { get; }
}

public interface IChannelCallback
{
    IHandlerPipeline? Pipeline { get; }
    
    /// <summary>
    ///     Mark the message as having been successfully received and processed
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask CompleteAsync(Envelope envelope);


    /// <summary>
    ///     Mark the incoming message as not processed
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    ValueTask DeferAsync(Envelope envelope);

    /// <summary>
    ///     Attempt to place this message back at the end of the channel queue
    /// </summary>
    /// <param name="envelope"></param>
    /// <returns></returns>
    Task<bool> TryRequeueAsync(Envelope envelope)
    {
        return Task.FromResult(false);
    }
}

/// <summary>
/// GH-4048. Marks a listener whose broker puts a clock on an <em>unsettled</em> delivery -- SQS's visibility
/// timeout, Azure Service Bus's lock duration, Pub/Sub's ack deadline, JetStream's AckWait -- so that
/// <see cref="Wolverine.Configuration.EndpointMode.NativeAck" /> can keep that clock alive for as long as the
/// envelope sits in an execution lane, rather than only while a handler runs.
/// </summary>
/// <remarks>
/// The tick timing, the ceiling, the loss classification and the enforcement all live in core, in
/// <c>LeaseRenewalTracker</c>. A transport contributes only the broker call plus the two durations that
/// describe its clock. The matching endpoint-side declaration is <c>Endpoint.holdsExpiringLease</c>, and
/// a NativeAck endpoint that declares an expiring lease without a listener implementing this interface
/// fails at startup rather than silently generating duplicates.
/// </remarks>
public interface ISupportLeaseRenewal
{
    /// <summary>
    ///     How long the broker's clock runs on one unsettled delivery. Each successful renewal re-arms it
    ///     for this long, and the tracker ticks at half of it.
    /// </summary>
    TimeSpan LeaseDuration { get; }

    /// <summary>
    ///     Longest a single delivery may be kept alive, measured from its receipt. Broker-capped -- SQS, for
    ///     instance, will not keep a message invisible for more than 12 hours. Reaching this stops renewal;
    ///     it is deliberately NOT treated as a lost lease, because the delivery may still finish inside the
    ///     lease it already holds.
    /// </summary>
    TimeSpan MaximumLeaseExtension { get; }

    /// <summary>
    ///     False when the transport's own client already renews for the whole time a delivery is unsettled
    ///     (Pub/Sub's SubscriberClient does). Wolverine then issues no renewal calls at all and enforces only
    ///     the ceiling.
    /// </summary>
    bool RequiresExplicitRenewal => true;

    /// <summary>
    ///     Re-arm the broker's clock on these deliveries. Returns the envelopes the broker would <b>not</b>
    ///     renew -- their lease is gone and the broker owns them again.
    /// </summary>
    /// <remarks>
    ///     Throwing is a <em>transient</em> failure (network, throttle) and is explicitly NOT a lost lease;
    ///     the tracker keeps those envelopes and retries on the next tick. Report a lost lease only by
    ///     returning the envelope in the result.
    /// </remarks>
    ValueTask<IReadOnlyList<Envelope>> RenewLeasesAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token);
}