using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Continuation that pauses the sending agent for a specified duration, then automatically
/// resumes sending. Similar to PauseListenerContinuation but for outgoing message senders.
/// </summary>
internal class PauseSendingContinuation : IContinuation, IContinuationSource
{
    public PauseSendingContinuation(TimeSpan pauseTime)
    {
        PauseTime = pauseTime;
    }

    public TimeSpan PauseTime { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        var envelope = lifecycle.Envelope;
        if (envelope?.Destination == null)
        {
            return;
        }

        var agent = runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination);
        if (agent is SendingAgent sendingAgent)
        {
            activity?.AddEvent(new ActivityEvent(WolverineTracing.SendingPaused));

            await sendingAgent.PauseAsync(PauseTime);
        }
        else
        {
            runtime.Logger.LogInformation(
                "Unable to pause sending agent for {Destination}.",
                envelope.Destination);
        }
    }

    public string Description => "Pause sending for " + PauseTime;

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}
