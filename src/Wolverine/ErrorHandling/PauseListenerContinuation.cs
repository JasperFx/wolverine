using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Tracing;
using Wolverine.Transports;

namespace Wolverine.ErrorHandling;

internal class PauseListenerContinuation : IContinuation, IContinuationSource
{
    public PauseListenerContinuation(TimeSpan pauseTime)
    {
        PauseTime = pauseTime;
    }

    public TimeSpan PauseTime { get; }

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        IListenerCircuit? agent;
        var destination = lifecycle.Envelope!.Destination;
        if (destination?.Scheme == "local")
        {
            // This will only work for durable, local queues
            agent = runtime.Endpoints.GetOrBuildSendingAgent(destination) as IListenerCircuit;
        }
        else
        {
            agent = runtime.Endpoints.FindListeningAgent(lifecycle.Envelope!.Listener!.Address);
        }

        if (agent != null)
        {
            activity?.AddEvent(new ActivityEvent(WolverineTracing.PausedListener));

#pragma warning disable VSTHRD110
            Task.Factory.StartNew(() =>
#pragma warning restore VSTHRD110
                agent.PauseAsync(PauseTime), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
        else
        {
            runtime.Logger.LogInformation(
                "Unable to pause listening endpoint {Destination}. Is this a local queue that is not durable?",
                destination);
        }

        return ValueTask.CompletedTask;
    }

    public string Description => "Pause all message processing on this listener for " + PauseTime;

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}