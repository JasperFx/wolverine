using System;
using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

public class RequeueContinuation : IContinuation, IContinuationSource
{
    public static readonly RequeueContinuation Instance = new();

    private RequeueContinuation()
    {
    }

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now)
    {
        return lifecycle.DeferAsync();
    }

    public override string ToString()
    {
        return "Defer the message for later processing";
    }

    public string Description { get; } = "Defer or Re-queue the message for later processing";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }
}
