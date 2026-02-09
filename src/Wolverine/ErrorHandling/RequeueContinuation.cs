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
            // First, defer/requeue the message back to the transport
            await lifecycle.DeferAsync();

            // Schedule the listener pause on a background task to avoid deadlocking
            // the BufferedReceiver's DrainAsync (which waits for in-flight messages,
            // including this one, to complete).
            var envelope = lifecycle.Envelope!;
            _ = Task.Run(async () =>
            {
                try
                {
                    var agent = findListenerCircuit(envelope, runtime);
                    if (agent != null)
                    {
                        await agent.PauseAsync(Delay.Value);
                    }
                }
                catch (Exception e)
                {
                    runtime.Logger.LogError(e, "Error pausing listener for PauseThenRequeue");
                }
            });
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