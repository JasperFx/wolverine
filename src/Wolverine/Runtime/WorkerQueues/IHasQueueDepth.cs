namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// GH-4186. Optional capability for an <see cref="Wolverine.Transports.IReceiver"/> that holds deliveries in an
/// in-memory structure and can say how deep it currently is. <see cref="ILocalQueue"/> extends this, so
/// <c>BufferedReceiver</c> and <c>DurableReceiver</c> satisfy it for free; the receivers that are deliberately
/// *not* local queues -- <c>InlineReceiver</c> and <c>NativeAckReceiver</c> -- implement it directly.
///
/// <para>
/// This is a separate interface rather than a widening of <see cref="ILocalQueue"/> because
/// <c>ListeningAgent.EnqueueDirectlyAsync</c> type-switches on <see cref="ILocalQueue"/> to decide how a replayed
/// envelope re-enters a listener, and a native-ack receiver needs its own branch there (GH-4011) precisely
/// because it must not be enqueued into like a local queue. Reporting a depth had to be separable from being one.
/// </para>
/// </summary>
public interface IHasQueueDepth
{
    /// <summary>
    /// How many messages this receiver is currently holding in memory. Read by <c>ListeningAgent.QueueCount</c>,
    /// which is what reaches <see cref="Wolverine.Configuration.EndpointHealthSnapshot.QueueCount"/> and the
    /// back pressure checks.
    /// </summary>
    int QueueCount { get; }

    /// <summary>
    /// GH-4186. Approximate timestamp of the last delivery this receiver accepted, stamped on receipt rather than
    /// inferred from a depth change. Null for receivers that do not stamp it, in which case
    /// <c>ListeningAgent.LastQueueActivityAt</c> falls back to the <c>BackPressureAgent</c>-driven change-detection
    /// heuristic. The modes that have no <c>BackPressureAgent</c> at all -- <c>Inline</c> and <c>NativeAck</c>,
    /// see <see cref="Wolverine.Configuration.Endpoint.ShouldEnforceBackPressure"/> -- stamp it, because for them
    /// nothing else ever would.
    /// </summary>
    DateTimeOffset? LastReceivedAt => null;

    /// <summary>
    /// GH-4199. Per-lane depth for a receiver whose execution block is partitioned by group id, or null when it
    /// is not partitioned at all. <see cref="QueueCount"/> alone is the SUM across lanes, and the sum is
    /// precisely the number that cannot see the failure the partitioning exists to bound: 100 messages spread
    /// evenly over 10 lanes and 100 messages piled into one lane report the identical depth, and the second is
    /// a stalled listener while the first is a healthy one.
    ///
    /// <para>
    /// <b>Implementers behind a wrapper must delegate this.</b> <c>ReceiverWithRules</c> is installed for
    /// something as ordinary as an endpoint-level <c>MessageType</c>, so leaving the default in place there
    /// would report "not partitioned" for most real endpoints. See <c>ReceiverWithRules</c> and
    /// <c>GlobalPartitionedInterceptor</c>, which delegate <see cref="QueueCount"/> for the same reason.
    /// </para>
    /// </summary>
    PartitionedLaneDepth? LaneDepth => null;
}

/// <summary>
/// GH-4199. A point-in-time read of how work is distributed across a partitioned receiver's lanes.
/// </summary>
/// <param name="LaneCount">How many partition slots the block was built with.</param>
/// <param name="BusiestLaneCount">
/// The deepest single partition slot. Read against <see cref="LaneCount"/> and the endpoint's total depth,
/// this is what makes "one dominant GroupId is serializing everything behind it" -- the failure GH-3899's
/// exempt lane exists to mitigate -- visible from outside the process.
/// </param>
/// <param name="ExemptLaneCount">
/// Depth of the GH-3899 exempt lane, or null when the endpoint has no exemptions. Deliberately NOT folded
/// into <paramref name="BusiestLaneCount"/>: the exempt lane runs with its own parallelism, so a healthy
/// multi-worker lane holding plenty of messages would otherwise be misread as a hot partition.
/// </param>
public record PartitionedLaneDepth(int LaneCount, int BusiestLaneCount, int? ExemptLaneCount);
