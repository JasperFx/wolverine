using System.Diagnostics;
using Wolverine.ErrorHandling;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Built-in continuation source for the batch member-isolation error policy
/// (<c>OnException&lt;T&gt;().IsolateBatchMembers()</c>). Used for <em>opaque</em> batch failures where the
/// handler cannot name the bad item: re-run each member as its own size-1 batch so only the one carrying
/// the bad element dead-letters and the healthy members succeed. See GH-3289.
/// </summary>
internal class ProbeIndividuallyContinuationSource : IContinuationSource
{
    public string Description => "Isolate the failing batch member by re-running each member individually";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new ProbeIndividuallyContinuation(ex);
    }
}

internal class ProbeIndividuallyContinuation : IContinuation
{
    private readonly Exception _exception;

    public ProbeIndividuallyContinuation(Exception exception)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        var batch = lifecycle.Envelope;
        var members = batch?.Batch;

        // A single-member "batch" is already isolated -> dead-letter it. This is also the terminating
        // case: a multi-item batch is probed into singletons, and each failing singleton lands here.
        if (batch is null || members is null || members.Length <= 1)
        {
            await lifecycle.MoveToDeadLetterQueueAsync(_exception).ConfigureAwait(false);
            await lifecycle.CompleteAsync().ConfigureAwait(false);
            if (batch is not null)
            {
                runtime.MessageTracking.MessageFailed(batch, _exception);
            }

            return;
        }

        // Re-run each member as its own size-1 batch (via the shared re-execution primitive) so only the
        // member carrying the bad element fails and dead-letters; the healthy members succeed. Bounded
        // and one-time: the singletons cannot be split further (handled by the guard above).
        foreach (var member in members)
        {
            await BatchReplay.EnqueueReducedBatchAsync(runtime, batch, new[] { member.Message! })
                .ConfigureAwait(false);
        }

        // Settle the original batch; every member is now re-represented as its own singleton batch.
        await lifecycle.CompleteAsync().ConfigureAwait(false);

        runtime.MessageTracking.MessageFailed(batch, _exception);
        activity?.AddEvent(new ActivityEvent("Wolverine.Batch.ProbedIndividually"));
    }
}
