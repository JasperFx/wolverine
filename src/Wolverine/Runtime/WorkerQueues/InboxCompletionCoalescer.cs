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
                batch = _pending.GetRange(0, take).ToArray();
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
                await _markBatch(batch.Select(x => x.Envelope).ToList()).ConfigureAwait(false);
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
