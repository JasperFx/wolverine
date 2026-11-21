namespace Wolverine.Transports.Local;

/// <summary>
/// GH-5700. A local queue that a tracked session's playback can hand a due scheduled message to. The message goes
/// through the queue's own receiving pipeline, with the queue's endpoint, error policies and dead letter handling, as
/// if its time had come. It runs in the caller's async flow rather than on the queue's worker, so a test clock kept in
/// an <see cref="System.Threading.AsyncLocal{T}" /> (Bobcat's shared clock) is still the time the handler reads.
/// </summary>
internal interface IPlaysScheduledMessages
{
    Task PlayNowAsync(Envelope envelope);
}
