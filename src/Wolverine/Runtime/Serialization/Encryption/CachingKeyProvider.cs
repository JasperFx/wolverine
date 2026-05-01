namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Wraps an inner <see cref="IKeyProvider"/> with a per-key TTL cache and
/// single-flight deduplication of concurrent requests for the same key-id.
/// The cache is bounded by an LRU policy with a configurable maximum number
/// of entries (default 1024); deployments that need to keep more keys hot
/// (e.g. many tenants with per-tenant keys) should raise this limit.
/// <see cref="DefaultKeyId"/> is forwarded to the inner without caching.
/// </summary>
public sealed class CachingKeyProvider : IKeyProvider
{
    private readonly IKeyProvider _inner;
    private readonly TimeSpan _ttl;
    private readonly LruEntryStore _cache;

    public CachingKeyProvider(IKeyProvider inner, TimeSpan ttl, int maxEntries = 1024)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be positive.");

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl;
        _cache = new LruEntryStore(maxEntries);
    }

    public string DefaultKeyId => _inner.DefaultKeyId;

    public async ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _cache.GetOrAdd(keyId, id => new CacheEntry(FetchAsync(id)));

            if (entry.IsExpired(_ttl))
            {
                _cache.TryRemove(keyId, entry);
                continue;
            }

            try
            {
                return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Per-caller cancellation: leave the shared inner task untouched
                // so other waiters still see the value when the inner completes.
                throw;
            }
            catch
            {
                _cache.TryRemove(keyId, entry);
                throw;
            }
        }
    }

    private async Task<byte[]> FetchAsync(string keyId)
    {
        return await _inner.GetKeyAsync(keyId, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class CacheEntry
    {
        public Task<byte[]> Task { get; }
        public DateTimeOffset CreatedAt { get; }

        public CacheEntry(Task<byte[]> task)
        {
            Task = task;
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public bool IsExpired(TimeSpan ttl)
            => DateTimeOffset.UtcNow - CreatedAt > ttl;
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
