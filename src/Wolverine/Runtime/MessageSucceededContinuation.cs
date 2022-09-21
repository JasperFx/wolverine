using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;

namespace Wolverine.Runtime;

public class MessageSucceededContinuation : IContinuation
{
    public static readonly MessageSucceededContinuation Instance = new();

    private MessageSucceededContinuation()
    {
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now)
    {
        try
        {
            await lifecycle.FlushOutgoingMessagesAsync();

            await lifecycle.CompleteAsync();

            runtime.MessageLogger.MessageSucceeded(lifecycle.Envelope!);
        }
        catch (Exception ex)
        {
            await lifecycle.SendFailureAcknowledgementAsync("Sending cascading message failed: " + ex.Message);

            runtime.Logger.LogError(ex, "Failure while post-processing a successful envelope");
            runtime.MessageLogger.MessageFailed(lifecycle.Envelope!, ex);

            await new MoveToErrorQueue(ex).ExecuteAsync(lifecycle, runtime, now);
        }
    }
}
