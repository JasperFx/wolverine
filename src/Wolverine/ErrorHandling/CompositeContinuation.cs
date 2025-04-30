using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.ErrorHandling;

internal class CompositeContinuation : IContinuation, IInlineContinuation
{
    private readonly IContinuation[] _continuations;

    public CompositeContinuation(params IContinuation[] continuations)
    {
        _continuations = continuations;
    }

    public IReadOnlyList<IContinuation> Inner => _continuations;

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        foreach (var continuation in _continuations)
        {
            try
            {
                await continuation.ExecuteAsync(lifecycle, runtime, now, activity);
            }
            catch (Exception e)
            {
                runtime.Logger.LogError(e,
                    "Failed while attempting to apply continuation {Continuation} on Envelope {Envelope}", continuation,
                    lifecycle.Envelope);
            }
        }
    }

    public async ValueTask<InvokeResult> ExecuteInlineAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity, CancellationToken cancellation)
    {
        var inners = _continuations.OfType<IInlineContinuation>().ToArray();
        if (inners.Any())
        {
            var results = new InvokeResult[inners.Length];
            for (int i = 0; i < inners.Length; i++)
            {
                try
                {
                    results[i] = await inners[i].ExecuteInlineAsync(lifecycle, runtime, now, activity, cancellation);
                }
                catch (Exception e)
                {
                    results[i] = InvokeResult.Stop;
                    runtime.Logger.LogError(e,
                        "Failed while attempting to apply inline continuation {Continuation} on Envelope {Envelope}", inners[i],
                        lifecycle.Envelope);
                }
            }

            if (results.All(x => x == InvokeResult.TryAgain)) return InvokeResult.TryAgain;
        }
        
        return InvokeResult.Stop;
    }
}