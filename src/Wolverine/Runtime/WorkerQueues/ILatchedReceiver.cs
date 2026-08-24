namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// A receiver that can be latched -- told to stop executing anything further -- ahead of
/// <see cref="Wolverine.Transports.IReceiver.DrainAsync" />.
/// </summary>
/// <remarks>
/// <para>GH-3709. This exists so <c>ListeningAgent.LatchReceiver()</c> is a single type test rather than an
/// if/else chain naming each receiver implementation. The chain had already silently missed
/// <see cref="NativeAckReceiver" /> when GH-3708 added it, and the failure was invisible rather than loud:
/// an unlatched receiver's <c>DrainAsync</c> returns immediately instead of waiting for in-flight handlers,
/// so a stop-and-drain closed the transport channel underneath still-running work. Every unsettled delivery
/// was then requeued and redelivered <i>while the original was still executing</i> -- which on an exclusive
/// listener handoff means the new owner runs a message concurrently with the old owner, breaking the
/// no-two-messages-of-a-group-at-once guarantee that partitioned processing exists to provide.</para>
/// </remarks>
internal interface ILatchedReceiver
{
    /// <summary>
    /// Stop executing further messages. Does not wait for in-flight work -- that is <c>DrainAsync</c>'s job,
    /// and it only waits when the receiver has been latched first.
    /// </summary>
    void Latch();
}
