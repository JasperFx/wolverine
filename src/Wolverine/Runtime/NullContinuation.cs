using System;
using System.Threading.Tasks;

namespace Wolverine.Runtime;

internal class NullContinuation : IContinuation
{
    public static readonly NullContinuation Instance = new();

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now)
    {
        return ValueTask.CompletedTask;
    }
}