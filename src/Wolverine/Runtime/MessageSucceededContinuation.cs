using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Logging;

namespace Wolverine.Runtime;

public class MessageSucceededContinuation : IContinuation
{
    public static readonly MessageSucceededContinuation Instance = new();

    private MessageSucceededContinuation()
    {
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        try
        {
            await lifecycle.FlushOutgoingMessagesAsync();

            await lifecycle.CompleteAsync();

            lifecycle.CompletionTrackerFor(runtime).MessageSucceeded(lifecycle.Envelope!);
        }
        catch (Exception ex)
        {
            await lifecycle.SendFailureAcknowledgementAsync("Sending cascading message failed: " + ex.Message);

            runtime.Logger.LogError(ex, "Failure while post-processing a successful envelope");

            // GH-4136: no MessageFailed call here. MoveToErrorQueue.ExecuteAsync -- the very next line --
            // reports both MovedToErrorQueue and MessageFailed itself, so calling it here too
            // double-incremented the dead-letter counter, recorded effective time twice, and fired the
            // failure wire tap twice for one envelope. Now that MessageFailed records a terminal
            // MessageEventType it would also have completed the tracked envelope before
            // MoveToErrorQueue could record the MovedToErrorQueue event a caller asserts on.
            await new MoveToErrorQueue(ex).ExecuteAsync(lifecycle, runtime, now, activity);
        }
    }
}