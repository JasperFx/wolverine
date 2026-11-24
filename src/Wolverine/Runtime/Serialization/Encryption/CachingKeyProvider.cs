using System.Diagnostics;

namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Wraps an inner <see cref="IKeyProvider"/> with a per-key TTL cache and
/// single-flight deduplication of concurrent requests for the same key-id.
/// The cache is bounded by an LRU policy with a configurable maximum number
/// of entries (default 1024); deployments that need to keep more keys hot
/// (e.g. many tenants with per-tenant keys) should raise this limit.
/// <see cref="DefaultKeyId"/> is forwarded to the inner without caching.
/// </summary>
/// <remarks>
/// <para>By default a refresh that fails after the TTL expires throws. The
/// encrypting serializer wraps any exception other than <see cref="OperationCanceledException"/>
/// from the key provider in <see cref="EncryptionKeyNotFoundException"/>. Either way an
/// inbound message is dead-lettered and an outbound send fails. With
/// <c>serveLastKnownKeyOnRefreshFailure</c> the cache instead serves the bytes it
/// last resolved for that key-id, because a key-id's bytes never change. Only a
/// <see cref="KeyNotFoundException"/> from the inner provider is taken to mean the
/// key is gone; it still throws and evicts the entry, and no refresh of that key-id
/// falls back on bytes resolved before it. Each later call retries the inner provider
/// until a refresh succeeds.</para>
/// </remarks>
public sealed class CachingKeyProvider : IKeyProvider
{
    private readonly IKeyProvider _inner;
    private readonly TimeSpan _ttl;
    private readonly LruEntryStore _cache;
    private readonly bool _serveLastKnownKeyOnRefreshFailure;

    /// <param name="inner">The provider that resolves keys, typically a remote KMS adapter.</param>
    /// <param name="ttl">How long resolved key bytes are served before the inner provider is asked again.</param>
    /// <param name="maxEntries">The most key-ids kept; the least recently used is evicted beyond this.</param>
    public CachingKeyProvider(IKeyProvider inner, TimeSpan ttl, int maxEntries = 1024)
        : this(inner, ttl, serveLastKnownKeyOnRefreshFailure: false, maxEntries)
    {
    }

    // A separate overload rather than a new optional parameter on the constructor above,
    // so assemblies compiled against the three-parameter constructor keep binding to it.
    /// <param name="inner">The provider that resolves keys, typically a remote KMS adapter.</param>
    /// <param name="ttl">How long resolved key bytes are served before the inner provider is asked again.</param>
    /// <param name="serveLastKnownKeyOnRefreshFailure">
    /// When true, a refresh after the TTL expires that fails with anything but a
    /// <see cref="KeyNotFoundException"/> serves the key-id's last known bytes instead of throwing.
    /// A key-id that has never resolved still throws.
    /// </param>
    /// <param name="maxEntries">The most key-ids kept; the least recently used is evicted beyond this.</param>
    public CachingKeyProvider(IKeyProvider inner, TimeSpan ttl, bool serveLastKnownKeyOnRefreshFailure,
        int maxEntries = 1024)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be positive.");

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl;
        _cache = new LruEntryStore(maxEntries);
        _serveLastKnownKeyOnRefreshFailure = serveLastKnownKeyOnRefreshFailure;
    }

    public string DefaultKeyId => _inner.DefaultKeyId;

    public async ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        var entry = _cache.GetOrAdd(keyId, StartColdFetch);

        if (entry.IsExpired(_ttl))
        {
            // Replaced in place rather than removed, so no caller can slip in a
            // refresh that has lost the last known bytes. The refreshed entry is
            // awaited without another expiry check: one that fell back on the last
            // known bytes is already expired, and re-checking would refresh forever.
            // A key-id the store has already dropped, for example after a KeyNotFoundException,
            // is fetched cold, with nothing to fall back on.
            var expired = entry;
            entry = _cache.Replace(keyId, expired, id => StartRefresh(id, expired))
                    ?? _cache.GetOrAdd(keyId, StartColdFetch);
        }

        try
        {
            var fetched = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return fetched.Key;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Per-caller cancellation: leave the shared inner task untouched
            // so other waiters still see the value when the inner completes.
            // A cancellation from the inner provider is its own timeout, and is
            // evicted below like any other failure.
            throw;
        }
        catch
        {
            _cache.TryRemove(keyId, entry);
            throw;
        }
    }

    private CacheEntry StartColdFetch(string keyId)
    {
        var revocations = new KeyRevocations();
        return new CacheEntry(FetchAsync(keyId, lastKnownKey: null, revocations, revocationsAtStart: 0),
            startedWithKey: null, revocations, revocationsAtStart: 0);
    }

    private CacheEntry StartRefresh(string keyId, CacheEntry expired)
    {
        // The count is read before the bytes, so a revocation between the two reads
        // still stops this refresh falling back on them.
        var revocations = expired.Revocations;
        var revocationsAtStart = revocations.Current;
        var lastKnownKey = _serveLastKnownKeyOnRefreshFailure ? expired.LastKnownKey : null;
        return new CacheEntry(FetchAsync(keyId, lastKnownKey, revocations, revocationsAtStart),
            lastKnownKey, revocations, revocationsAtStart);
    }

    private async Task<FetchedKey> FetchAsync(string keyId, byte[]? lastKnownKey, KeyRevocations revocations,
        long revocationsAtStart)
    {
        try
        {
            var key = await _inner.GetKeyAsync(keyId, CancellationToken.None).ConfigureAwait(false);
            return new FetchedKey(key, IsLastKnown: false);
        }
        catch (KeyNotFoundException)
        {
            // Recorded here rather than by a waiting caller, so it takes effect even when
            // no caller is still waiting on this refresh.
            revocations.Revoke();
            throw;
        }
        // The inner provider is called without a token, so an OperationCanceledException
        // here is its own timeout, as transient as any other failure.
        catch (Exception ex) when (lastKnownKey is not null && revocations.Current == revocationsAtStart)
        {
            Activity.Current?.AddEvent(new ActivityEvent(WolverineTracing.EncryptionKeyLastKnownServed,
                tags: new ActivityTagsCollection
                {
                    [WolverineTracing.EncryptionKeyId] = keyId,
                    ["exception.type"] = ex.GetType().FullName
                }));

            return new FetchedKey(lastKnownKey, IsLastKnown: true);
        }
    }

    private readonly record struct FetchedKey(byte[] Key, bool IsLastKnown);

    // Refreshes of one key-id can overlap when one outlives its TTL, and each entry
    // carries this forward from the one it replaces. A KeyNotFoundException in any of
    // them then reaches the others: no refresh that started before it falls back.
    private sealed class KeyRevocations
    {
        private long _count;

        public long Current => Volatile.Read(ref _count);

        public void Revoke() => Interlocked.Increment(ref _count);
    }

    private sealed class CacheEntry
    {
        public Task<FetchedKey> Task { get; }
        public DateTimeOffset CreatedAt { get; }
        public KeyRevocations Revocations { get; }
        private readonly long _revocationsAtStart;
        private readonly byte[]? _startedWithKey;

        public CacheEntry(Task<FetchedKey> task, byte[]? startedWithKey, KeyRevocations revocations,
            long revocationsAtStart)
        {
            Task = task;
            CreatedAt = DateTimeOffset.UtcNow;
            Revocations = revocations;
            _revocationsAtStart = revocationsAtStart;
            _startedWithKey = startedWithKey;
        }

        /// <summary>
        /// The bytes this entry resolved, whether fetched or served as the last known key,
        /// or the bytes it started with if its refresh hasn't completed yet. None if its
        /// fetch failed or the key-id was revoked after it started.
        /// </summary>
        public byte[]? LastKnownKey
            => Task.IsFaulted || Task.IsCanceled || Revocations.Current != _revocationsAtStart
                ? null
                : Resolved?.Key ?? _startedWithKey;

        // An entry that fell back on the last known bytes is expired at once, so the next
        // caller retries the inner provider instead of serving stale bytes for a whole TTL.
        public bool IsExpired(TimeSpan ttl)
            => Resolved is { IsLastKnown: true }
               || DateTimeOffset.UtcNow - CreatedAt > ttl;

        private FetchedKey? Resolved
        {
            get
            {
                if (!Task.IsCompletedSuccessfully) return null;
#pragma warning disable VSTHRD002 // The task has completed, so reading its result never blocks
                return Task.Result;
#pragma warning restore VSTHRD002
            }
        }
    }

    private sealed class LruEntryStore
    {
        // _index gives O(1) lookup; _order tracks recency (head = MRU, tail = LRU).
        private readonly int _maxEntries;
        private readonly object _lock = new();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, CacheEntry>>> _index = new();
        private readonly LinkedList<KeyValuePair<string, CacheEntry>> _order = new();

        public LruEntryStore(int maxEntries)
        {
            _maxEntries = maxEntries;
        }

        public CacheEntry GetOrAdd(string keyId, Func<string, CacheEntry> factory)
        {
            lock (_lock)
            {
                if (_index.TryGetValue(keyId, out var existing))
                {
                    _order.Remove(existing);
                    _order.AddFirst(existing);
                    return existing.Value.Value;
                }

                if (_index.Count >= _maxEntries)
                {
                    var evicted = _order.Last!;
                    _order.RemoveLast();
                    _index.Remove(evicted.Value.Key);
                }

                // The factory must be non-blocking — it wraps a hot Task, not awaits one.
                // Anything heavier here would hold the lock for the duration of I/O.
                var newEntry = factory(keyId);
                var node = new LinkedListNode<KeyValuePair<string, CacheEntry>>(
                    new KeyValuePair<string, CacheEntry>(keyId, newEntry));
                _order.AddFirst(node);
                _index[keyId] = node;
                return newEntry;
            }
        }

        /// <summary>
        /// Replaces <paramref name="expired"/> with a new entry and returns it. If another
        /// caller has already replaced it, returns the entry now cached instead. If the
        /// key-id has been removed, returns null and adds nothing.
        /// </summary>
        public CacheEntry? Replace(string keyId, CacheEntry expired, Func<string, CacheEntry> factory)
        {
            lock (_lock)
            {
                if (!_index.TryGetValue(keyId, out var node))
                {
                    return null;
                }

                if (ReferenceEquals(node.Value.Value, expired))
                {
                    // Same non-blocking factory contract as GetOrAdd.
                    node.Value = new KeyValuePair<string, CacheEntry>(keyId, factory(keyId));
                }

                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Value;
            }
        }

        public bool TryRemove(string keyId, CacheEntry expected)
        {
            lock (_lock)
            {
                if (_index.TryGetValue(keyId, out var node)
                    && ReferenceEquals(node.Value.Value, expected))
                {
                    _order.Remove(node);
                    _index.Remove(keyId);
                    return true;
                }
                return false;
            }
        }
    }
}
