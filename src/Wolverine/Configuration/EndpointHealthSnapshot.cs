using Wolverine.Transports;

namespace Wolverine.Configuration;

/// <summary>
/// A point-in-time snapshot of health state for a single messaging endpoint.
/// </summary>
/// <param name="BufferLimit">
/// The local buffering ceiling, and <b>only</b> populated when the endpoint actually enforces it. GH-4199:
/// this used to be filled in for every listener regardless of mode, but
/// <see cref="Endpoint.ShouldEnforceBackPressure"/> is false on <see cref="EndpointMode.Inline"/> and
/// <see cref="EndpointMode.NativeAck"/>, so no BackPressureAgent is built and nothing ever reads it there.
/// Once GH-4186 made <see cref="QueueCount"/> real for those two modes the pair started rendering as
/// "234 of 1,000" and offering headroom that does not exist. Null now means "this endpoint has no local
/// buffering ceiling", and a consumer should look to <see cref="InFlightLimit"/> instead of reimplementing
/// <c>ShouldEnforceBackPressure</c> from a serialized mode name.
/// </param>
/// <param name="LaneCount">
/// GH-4199. Number of partition slots when this listener is group-partitioned, otherwise null.
/// </param>
/// <param name="BusiestLaneCount">
/// GH-4199. The deepest single partition slot. <see cref="QueueCount"/> is the SUM across lanes, and the sum
/// cannot see the failure the partitioning exists to bound: 100 messages spread over 10 lanes and 100
/// messages piled into one lane report the identical depth, and the second is a stalled listener. Compare
/// this against <see cref="QueueCount"/> and <see cref="LaneCount"/> to tell them apart.
/// </param>
/// <param name="ExemptLaneCount">
/// GH-4199. Depth of the GH-3899 exempt lane, or null when the endpoint has no exemptions. Reported apart
/// from <see cref="BusiestLaneCount"/> on purpose: the exempt lane has its own parallelism, so folding it in
/// would misreport a healthy multi-worker lane as a hot partition.
/// </param>
/// <param name="InFlightLimit">
/// GH-4199. The ceiling that does bound the endpoint when back pressure is not enforced -- normally the
/// broker's prefetch window. Null when the transport has no such ceiling, which is a real answer rather than
/// a missing one: render the depth with no denominator rather than inventing one.
/// </param>
public record EndpointHealthSnapshot(
    Uri Uri,
    string EndpointName,
    EndpointDirection Direction,
    string Status,
    int QueueCount,
    DateTimeOffset? LastQueueActivityAt,
    DateTimeOffset? LastMessageSentAt,
    bool SenderLatched,
    int? BufferLimit,
    long? BrokerQueueDepth = null,
    TransportConnectionState ConnectionState = TransportConnectionState.Unknown,
    ReceiveLoopStatus ReceiveLoopStatus = ReceiveLoopStatus.Unknown,
    DateTimeOffset? LastReceiveLoopActivityAt = null,
    int? InFlightLimit = null,
    int? LaneCount = null,
    int? BusiestLaneCount = null,
    int? ExemptLaneCount = null);

public enum EndpointDirection
{
    Listening,
    Sending
}
