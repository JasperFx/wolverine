using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Continuation that latches (pauses) the sending agent, similar to PauseListenerContinuation
/// but for outgoing message senders.
/// </summary>
internal class LatchSenderContinuation : IContinuation, IContinuationSource
{
    public static readonly LatchSenderContinuation Instance = new();

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        var envelope = lifecycle.Envelope;
        if (envelope?.Destination == null)
        {
            return;
        }

        var agent = runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination);
        if (agent is ISenderCircuit circuit)
        {
            activity?.AddEvent(new ActivityEvent(WolverineTracing.SendingPaused));

            // Use LatchAndDrainAsync if available on SendingAgent
            if (agent is SendingAgent sendingAgent)
            {
                await sendingAgent.LatchAndDrainAsync();
            }
        }
        else
        {
            runtime.Logger.LogInformation(
                "Unable to latch sending agent for {Destination}.",
                envelope.Destination);
        }
    }

    public string Description => "Latch (pause) the sending agent";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}
