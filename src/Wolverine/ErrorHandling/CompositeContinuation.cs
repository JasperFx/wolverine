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
        var allTryAgain = true;
        var hasInline = false;

        foreach (var continuation in _continuations)
        {
            if (continuation is IInlineContinuation inline)
            {
                hasInline = true;
                try
                {
                    var result = await inline.ExecuteInlineAsync(lifecycle, runtime, now, activity, cancellation);
                    if (result != InvokeResult.TryAgain)
                    {
                        allTryAgain = false;
                    }
                }
                catch (Exception e)
                {
                    allTryAgain = false;
                    runtime.Logger.LogError(e,
                        "Failed while attempting to apply inline continuation {Continuation} on Envelope {Envelope}", inline,
                        lifecycle.Envelope);
                }
            }
        }

        return hasInline && allTryAgain ? InvokeResult.TryAgain : InvokeResult.Stop;
    }
}