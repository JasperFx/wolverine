using System.Diagnostics;
using Wolverine.Runtime;

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
        if (Delay != null)
        {
            await Task.Delay(Delay.Value).ConfigureAwait(false);
        }
        
        activity?.AddEvent(new ActivityEvent(WolverineTracing.EnvelopeRequeued));
        await lifecycle.DeferAsync();
    }

    public string Description { get; } = "Defer or Re-queue the message for later processing";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }

    public override string ToString()
    {
        return "Defer the message for later processing";
    }
}