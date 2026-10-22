using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Transports;

namespace Wolverine.Runtime.Batching;

internal static class BatchDeadLetters
{
    /// <summary>
    /// F2. Dead letter the envelope -- a whole batch, or the singleton batch a probe ends with -- the way
    /// <see cref="MoveToErrorQueue"/> does since GH-5105: a write that could not be made has handed the envelope back
    /// to be executed again, and nothing may then complete it, which would settle, and so lose, the only copy
    /// </summary>
    /// <returns>False if the move failed and the envelope was handed back; the caller must not complete it</returns>
    public static async Task<bool> TryMoveToDeadLetterQueueAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime, Exception exception)
    {
        try
        {
            await lifecycle.MoveToDeadLetterQueueAsync(exception).ConfigureAwait(false);
            return true;
        }
        catch (DeadLetterMoveFailedException e)
        {
            runtime.Logger.LogWarning(e,
                "Could not move envelope {EnvelopeId} to the error queue; it was handed back to be executed again",
                lifecycle.Envelope?.Id);

            if (lifecycle.Envelope != null)
            {
                lifecycle.CompletionTrackerFor(runtime).Requeued(lifecycle.Envelope);
            }

            return false;
        }
    }
}
