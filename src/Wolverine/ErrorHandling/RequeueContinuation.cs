using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.ErrorHandling;

internal class RequeueContinuation : IContinuation, IContinuationSource
{
    public static readonly RequeueContinuation Instance = new();

    private RequeueContinuation()
    {
    }

    internal RequeueContinuation(TimeSpan delay)
    {
        Delay = delay;
    }

    public TimeSpan? Delay { get; }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeRequeued));

        if (Delay != null)
        {
            var envelope = lifecycle.Envelope!;
            var agent = findListenerCircuit(envelope, runtime);

            // For external transport listeners, stop the consumer BEFORE requeuing
            // to prevent the requeued message from being immediately re-consumed.
            // Without this, there is a race condition where the message is picked up
            // before the background PauseAsync can cancel the consumer.
            if (agent is ListeningAgent { Listener: not null } listeningAgent)
            {
                await listeningAgent.Listener.StopAsync();
            }

            // Defer/requeue the message back to the transport.
            // The consumer is already stopped, so the message will sit in the queue
            // until the listener restarts after the pause period.
            await lifecycle.DeferAsync();

            // Schedule the full pause cycle on a background task to avoid deadlocking
            // the BufferedReceiver's DrainAsync (which waits for in-flight messages,
            // including this one, to complete). This drains remaining messages,
            // disposes the listener, and schedules a restart after Delay.
            if (agent != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await agent.PauseAsync(Delay.Value);
                    }
                    catch (Exception e)
                    {
                        runtime.Logger.LogError(e, "Error pausing listener for PauseThenRequeue");
                    }
                });
            }
        }
        else
        {
            await lifecycle.DeferAsync();
        }
    }

    private static IListenerCircuit? findListenerCircuit(Envelope envelope, IWolverineRuntime runtime)
    {
        var destination = envelope.Destination;
        if (destination?.Scheme == "local")
        {
            return runtime.Endpoints.AgentForLocalQueue(destination) as IListenerCircuit;
        }

        return runtime.Endpoints.FindListeningAgent(envelope.Listener!.Address);
    }

    public string Description => "Defer or Re-queue the message for later processing";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }

    public override string ToString()
    {
        return "Defer the message for later processing";
    }
}