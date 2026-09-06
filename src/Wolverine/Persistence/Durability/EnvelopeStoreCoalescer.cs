using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-4319. Coalesces concurrent single-envelope durability writes into one batched round trip,
///     without ever making a caller wait for other work to show up. This is the insert-side twin of
///     <see cref="Wolverine.Runtime.WorkerQueues.InboxCompletionCoalescer" /> and shares its shape: one
///     flush is in flight at a time, every write that arrives while it runs is taken by the next flush
///     as a single batch, and the task handed back to a caller completes only once that caller's own
///     envelope is really in the database.
/// </summary>
/// <remarks>
///     <para>
///     <b>There is deliberately no timer, and adding one would be a bug.</b> On the outbox path the store
///     gates the send -- <c>DurableSendingAgent</c> stores the envelope and only then posts it to the
///     sender -- so any max-age window in front of the store delays <i>delivery</i>, not just bookkeeping.
///     GH-3490 measured that exact shape at a 5,767ms transit p50. Here a lone write flushes immediately,
///     so trickle traffic pays precisely what it paid before (one round trip) and batches form from
///     concurrency alone: they can only appear when there was already a flush in flight to hide behind.
///     </para>
///     <para>
///     <b>Failures fall back to one envelope at a time, and the exception reaches its own caller.</b> That
///     matters more here than on the completion side. The batched
///     <c>StoreIncomingAsync(IReadOnlyList&lt;Envelope&gt;)</c> runs inside an explicit transaction and
///     rolls the whole batch back on a duplicate key, so a batch failure means nothing was written; the
///     per-envelope retry then produces the real per-envelope outcome. A caller that knows how to handle
///     <see cref="DuplicateIncomingEnvelopeException" /> for its own envelope keeps seeing exactly that
///     exception for exactly that envelope, and one poisoned envelope cannot fail its neighbours.
///     </para>
/// </remarks>
internal class EnvelopeStoreCoalescer
{
    private readonly Func<IReadOnlyList<Envelope>, Task> _storeBatch;
    private readonly Func<Envelope, Task> _storeOne;
    private readonly int _maximumBatchSize;
    private readonly ILogger _logger;
    private readonly Uri _uri;
    private readonly object _lock = new();
    private readonly List<Pending> _pending = new();
    private bool _flushing;
    private Task? _flushLoop;

    /// <param name="storeBatch">The batched write. May throw; a failure falls back to <paramref name="storeOne" /> per envelope</param>
    /// <param name="storeOne">The single-envelope write. Its exception is delivered to that envelope's caller</param>
    /// <param name="maximumBatchSize">Most envelopes in one flush</param>
    /// <param name="uri">Endpoint, for logging</param>
    /// <param name="logger"></param>
    public EnvelopeStoreCoalescer(Func<IReadOnlyList<Envelope>, Task> storeBatch, Func<Envelope, Task> storeOne,
        int maximumBatchSize, Uri uri, ILogger logger)
    {
        _storeBatch = storeBatch;
        _storeOne = storeOne;
        _maximumBatchSize = Math.Max(1, maximumBatchSize);
        _uri = uri;
        _logger = logger;
    }

    /// <summary>
    ///     Number of envelopes waiting for the next flush. Exposed for tests.
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
    ///     Write this envelope. The returned task completes only after the database has it -- whether by
    ///     the shared batch or by the per-envelope fallback -- and faults with whatever the per-envelope
    ///     write threw for this envelope.
    /// </summary>
    public Task StoreAsync(Envelope envelope)
    {
        var pending = new Pending(envelope,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

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
            // Off the caller's thread: under sustained load the loop keeps finding work, and whichever
            // caller happened to arrive first must not be conscripted into flushing everyone else's
            // writes forever.
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
        if (batch.Length == 1)
        {
            await storeOneAsync(batch[0]).ConfigureAwait(false);
            return;
        }

        try
        {
            var envelopes = new Envelope[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                envelopes[i] = batch[i].Envelope;
            }

            await _storeBatch(envelopes).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Debug, not Warning: the overwhelmingly common cause is one duplicate envelope in the
            // batch, which is ordinary traffic and whose real handling belongs to the caller that owns
            // that envelope. The per-envelope pass below is what decides each outcome.
            _logger.LogDebug(e,
                "Failed to store a batch of {Count} envelopes at {Uri}; falling back to storing them one at a time",
                batch.Length, _uri);

            foreach (var pending in batch)
            {
                await storeOneAsync(pending).ConfigureAwait(false);
            }

            return;
        }

        foreach (var pending in batch)
        {
            pending.Completion.TrySetResult();
        }
    }

    private async Task storeOneAsync(Pending pending)
    {
        try
        {
            await _storeOne(pending.Envelope).ConfigureAwait(false);
            pending.Completion.TrySetResult();
        }
        catch (Exception e)
        {
            // The caller owns this envelope and its failure handling -- a DuplicateIncomingEnvelopeException
            // has to reach the same catch block it reached before this coalescer existed.
            pending.Completion.TrySetException(e);
        }
    }

    private sealed record Pending(Envelope Envelope, TaskCompletionSource Completion);
}
