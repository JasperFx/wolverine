namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// GH-5964. A receiver that executes each message inside the transport listener's own delivery callback, so the
/// message is settled against that listener only once its handler and error policy have finished. A pause raised from
/// inside that callback must not dispose the listener before then: a Rabbit MQ channel closed under it drops the ack of
/// a discarded message, which then comes back and runs again once the listener resumes.
/// </summary>
internal interface IRunsOnTheListenersCallback
{
    /// <summary>
    /// Completes once no message is executing. Only meaningful after the receiver has been drained, which stops it
    /// taking on more; waits no longer than <paramref name="timeout" />.
    /// </summary>
    Task WaitForInFlightMessagesAsync(TimeSpan timeout);
}
