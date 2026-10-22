using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Built-in continuation source for the batch member-isolation error handling. Used for <em>opaque</em>
/// batch failures where the handler cannot name the bad item: re-run each member as its own size-1 batch
/// so only the one carrying the bad element dead-letters and the healthy members succeed. See GH-3289.
///
/// Two triggers install it: the exception-type policy
/// <c>OnException&lt;T&gt;().IsolateBatchMembers()</c> (probe on the first failure of that type) and the
/// count-based <c>BatchMessagesOf&lt;T&gt;(b =&gt; b.ProbeIndividuallyAfter(N))</c> (retry the whole batch,
/// then probe after N failures).
/// </summary>
internal class ProbeIndividuallyContinuationSource : IContinuationSource
{
    private readonly int _probeAfterAttempts;

    // probeAfterAttempts is the attempt on which to probe; earlier attempts retry the whole batch. The
    // default of 1 probes on the first failure (the IsolateBatchMembers policy).
    public ProbeIndividuallyContinuationSource(int probeAfterAttempts = 1)
    {
        _probeAfterAttempts = probeAfterAttempts;
    }

    public string Description => "Isolate the failing batch member by re-running each member individually";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return new ProbeIndividuallyContinuation(ex, _probeAfterAttempts);
    }
}

internal class ProbeIndividuallyContinuation : IContinuation
{
    private readonly Exception _exception;
    private readonly int _probeAfterAttempts;

    public ProbeIndividuallyContinuation(Exception exception, int probeAfterAttempts = 1)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
        _probeAfterAttempts = probeAfterAttempts;
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
            // F2. A member whose dead-letter write failed was handed back to be executed again; completing it now
            // would settle, and so lose, it
            if (!await BatchDeadLetters.TryMoveToDeadLetterQueueAsync(lifecycle, runtime, _exception)
                    .ConfigureAwait(false))
            {
                return;
            }

            await lifecycle.CompleteAsync().ConfigureAwait(false);
            if (batch is not null)
            {
                runtime.MessageTracking.MessageFailed(batch, _exception);
            }

            return;
        }

        // Count-based trigger (ProbeIndividuallyAfter): retry the WHOLE batch until it has failed
        // probeAfterAttempts times, then probe. The Executor increments Attempts before each handling, so
        // Attempts is the number of failures so far. IsolateBatchMembers uses the default of 1 (probe on
        // the first failure).
        if (batch.Attempts < _probeAfterAttempts)
        {
            await lifecycle.DeferAsync().ConfigureAwait(false);
            return;
        }

        // Re-run each member as its own size-1 batch (via the shared re-execution primitive) so only the
        // member carrying the bad element fails and dead-letters; the healthy members succeed. Bounded
        // and one-time: the singletons cannot be split further (handled by the guard above). F1: each singleton
        // carries the ORIGINAL member, taken out of this batch, so it is settled only at the end of its own run and
        // a poison member is dead lettered as itself
        var originals = members.ToArray();
        BatchReplay.Detach(batch, originals);
        foreach (var member in originals)
        {
            try
            {
                await BatchReplay.EnqueueReducedBatchAsync(runtime, batch, [member]).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Leave its inbox row, if it has one, as it is to be recovered. Settling it would lose it
                runtime.Logger.LogError(e,
                    "Unable to replay batch member {EnvelopeId} on its own while probing batch {BatchId}",
                    member.Id, batch.Id);
            }
        }

        // Settle the original batch, which no longer holds any member: each one rides its own singleton batch.
        await lifecycle.CompleteAsync().ConfigureAwait(false);

        runtime.MessageTracking.MessageFailed(batch, _exception);
        activity?.AddEvent(new ActivityEvent("Wolverine.Batch.ProbedIndividually"));
    }
}
