using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Logging;

namespace Wolverine.Runtime;

public class MessageSucceededContinuation : IContinuation
{
    public static readonly MessageSucceededContinuation Instance = new();

    private IMessageTracker? _tracker;

    private MessageSucceededContinuation()
    {
    }

    public MessageSucceededContinuation(IMessageTracker tracker)
    {
        _tracker = tracker;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        try
        {
            await lifecycle.FlushOutgoingMessagesAsync();

            await lifecycle.CompleteAsync();

            (_tracker ?? runtime.MessageTracking).MessageSucceeded(lifecycle.Envelope!);
        }
        catch (Exception ex)
        {
            await lifecycle.SendFailureAcknowledgementAsync("Sending cascading message failed: " + ex.Message);

            runtime.Logger.LogError(ex, "Failure while post-processing a successful envelope");
            runtime.MessageTracking.MessageFailed(lifecycle.Envelope!, ex);

            await new MoveToErrorQueue(ex).ExecuteAsync(lifecycle, runtime, now, activity);
        }
    }
}