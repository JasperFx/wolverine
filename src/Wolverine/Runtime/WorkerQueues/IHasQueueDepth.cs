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
}
