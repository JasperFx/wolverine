using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class PauseListenerContinuation : IContinuation, IContinuationSource
{
    public PauseListenerContinuation(TimeSpan pauseTime)
    {
        PauseTime = pauseTime;
    }

    public TimeSpan PauseTime { get; }

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now)
    {
        var agent = runtime.Endpoints.FindListeningAgent(lifecycle.Envelope!.Listener!.Address);


        if (agent != null)
        {
#pragma warning disable VSTHRD110
            Task.Factory.StartNew(() =>
#pragma warning restore VSTHRD110
                agent.PauseAsync(PauseTime), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        return ValueTask.CompletedTask;
    }

    public string Description => "Pause all message processing on this listener for " + PauseTime;

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}