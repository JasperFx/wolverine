namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// CritterWatch#942 — a receiver whose execution block can fault terminally (jasperfx#506) and say
/// so. A faulted block has no un-fault: every subsequent post throws, its QueueCount freezes (which
/// permanently latches a back-pressured listener), and the receive loop keeps polling into a dead
/// sink. <see cref="Transports.ListeningAgent"/> and <see cref="Transports.BackPressureAgent"/>
/// consult this to tear down and rebuild the receiver instead of reusing the corpse forever.
/// </summary>
internal interface IFaultTrackingReceiver
{
    bool HasFaulted { get; }
}
