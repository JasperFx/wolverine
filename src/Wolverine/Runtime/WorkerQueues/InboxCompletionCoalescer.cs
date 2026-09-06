using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
///     GH-3711 (O1b). Coalesces concurrent durable-inbox completions into one batched mark-as-handled
///     round trip while preserving the contract that <c>CompleteAsync</c> does not return until the
///     envelope really is <c>Handled</c> in the database. One flush is in flight at a time; every
///     completion that arrives while it runs is taken by the next flush as a single batch. There is no
///     timer: a lone completion is flushed immediately, so trickle traffic pays exactly what it paid
///     before (one round trip), and batches form from concurrency alone under load.
/// </summary>
/// <remarks>
///     Why not fire-and-forget behind a max-age window like the insert side: the pipeline records
///     <c>MessageSucceeded</c> for tracked sessions only after <c>CompleteAsync</c> returns, and a great
///     many tests -- Wolverine's and its users' -- assert inbox state the moment a tracked session
///     finishes. Decoupling the UPDATE from the completion broke that ordering (CI on the first cut
///     of #4025). Awaiting the shared flush keeps the ordering and still amortizes the round trip.
/// </remarks>
internal class InboxCompletionCoalescer
{
    private readonly Func<IReadOnlyList<Envelope>, Task> _markBatch;
    private readonly Func<Envelope, Task> _markOne;
    private readonly int _maximumBatchSize;
    private readonly ILogger _logger;
    private readonly Uri _uri;
    private readonly object _lock = new();

    // GH-4332 swapped this for a Queue on the theory that List.RemoveRange(0, n) memmoves the
    // remainder and so goes quadratic under a deep backlog. The theory is right -- and irrelevant
    // at the depths this actually runs at. PerfWaveBenchmarks ("GH-4332 Drain", swept across
    // depths) puts the crossover near 5,000 pending completions: below it the memmove is CHEAPER
    // than Queue.Dequeue's per-item overhead, and the Queue measured 1.28x SLOWER at depth 100 and
    // 1.29x at 1,000 -- which is the shape a healthy node runs at. So the Queue was reverted.
    //
    // The drain below is not what preceded GH-4332 either. That was GetRange(0, n).ToArray():
    // a List of n allocated and copied, then an array of n allocated and copied again. CopyTo
    // straight into the array the flush already needs is one allocation and one copy, and measures
    // fastest of the three at every depth below the crossover while keeping the ~18% allocation
    // reduction the Queue won at all of them.
    //
    // If a deployment is ever found sitting 10,000+ completions behind, the Queue is the right
    // answer there -- 4.9x faster at 50,000 -- and the benchmark table says so.
    private readonly List<Pending> _pending = new();
    private bool _flushing;
    private Task? _flushLoop;

    /// <param name="markBatch">The batched inbox call. May throw; a failure falls back to <paramref name="markOne" /> per envelope</param>
    /// <param name="markOne">The per-envelope, retried path. Expected never to throw (a RetryBlock swallows and retries)</param>
    /// <param name="maximumBatchSize">Most envelopes in one flush</param>
    /// <param name="uri">Endpoint, for logging</param>
    /// <param name="logger"></param>
    public InboxCompletionCoalescer(Func<IReadOnlyList<Envelope>, Task> markBatch, Func<Envelope, Task> markOne,
        int maximumBatchSize, Uri uri, ILogger logger)
    {
        _markBatch = markBatch;
        _markOne = markOne;
        _maximumBatchSize = Math.Max(1, maximumBatchSize);
        _uri = uri;
        _logger = logger;
    }

    /// <summary>
    ///     Number of completions waiting for the next flush. Exposed for tests.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    ///     Mark this envelope handled. The returned task completes only after the database has been told,
    ///     whether by the shared batch or by the per-envelope fallback.
    /// </summary>
    public Task MarkAsHandledAsync(Envelope envelope)
    {
        var pending = new Pending(envelope, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        var startLoop = false;
        lock (_lock)
        {
            _pending.Add(pending);
            if (!_flushing)
            {
                _flushing = true;
                startLoop = true;
            }
        }

        if (startLoop)
        {
            // Off the caller's thread: under sustained load the loop keeps finding work, and the worker
            // that happened to arrive first must not be conscripted into flushing everyone else's
            // completions forever.
            var loop = Task.Run(flushLoopAsync);
            lock (_lock)
            {
                _flushLoop = loop;
            }
        }

        return pending.Completion.Task;
    }

    /// <summary>
    ///     Wait for any flush in flight (and whatever it sweeps up) to finish. Bounded by the caller.
    /// </summary>
    public Task DrainAsync()
    {
        Task? loop;
        lock (_lock)
        {
            loop = _flushLoop;
        }

        return loop ?? Task.CompletedTask;
    }

    private async Task flushLoopAsync()
    {
        while (true)
        {
            Pending[] batch;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _flushing = false;
                    _flushLoop = null;
                    return;
                }

                var take = Math.Min(_maximumBatchSize, _pending.Count);
                batch = new Pending[take];
                _pending.CopyTo(0, batch, 0, take);
                _pending.RemoveRange(0, take);
            }

            await flushAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task flushAsync(Pending[] batch)
    {
        try
        {
            if (batch.Length == 1)
            {
                await _markOne(batch[0].Envelope).ConfigureAwait(false);
            }
            else
            {
                // Pre-sized array rather than a LINQ projection into a List: this runs once per
                // flush on every durable endpoint
                var envelopes = new Envelope[batch.Length];
                for (var i = 0; i < batch.Length; i++)
                {
                    envelopes[i] = batch[i].Envelope;
                }

                await _markBatch(envelopes).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Failed to mark a batch of {Count} envelopes as handled at {Uri}; falling back to marking them one at a time",
                batch.Length, _uri);

            foreach (var pending in batch)
            {
                try
                {
                    await _markOne(pending.Envelope).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    // The per-envelope path is a RetryBlock that is expected to swallow and retry;
                    // if it does throw, the completion still has to be released
                    _logger.LogError(inner, "Failed to mark envelope {EnvelopeId} as handled at {Uri}", pending.Envelope.Id, _uri);
                }
            }
        }

        foreach (var pending in batch)
        {
            pending.Completion.TrySetResult();
        }
    }

    private sealed record Pending(Envelope Envelope, TaskCompletionSource Completion);
}
