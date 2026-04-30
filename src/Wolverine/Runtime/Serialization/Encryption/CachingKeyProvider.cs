using System.Collections.Concurrent;

namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Wraps an inner <see cref="IKeyProvider"/> with a per-key TTL cache and
/// single-flight deduplication of concurrent requests for the same key-id.
/// <see cref="DefaultKeyId"/> is forwarded to the inner without caching
/// (it is a property read and not on the hot path).
/// </summary>
public sealed class CachingKeyProvider : IKeyProvider
{
    private readonly IKeyProvider _inner;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public CachingKeyProvider(IKeyProvider inner, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl;
    }

    public string DefaultKeyId => _inner.DefaultKeyId;

    public async ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _cache.GetOrAdd(keyId, id => new CacheEntry(FetchAsync(id, cancellationToken)));

            if (entry.IsExpired(_ttl))
            {
                // Race: another thread may have just inserted; if our remove
                // succeeds, fall through to re-fetch. Otherwise loop and read
                // the freshly inserted entry.
                _cache.TryRemove(KeyValuePair.Create(keyId, entry));
                continue;
            }

            try
            {
                return await entry.Task.ConfigureAwait(false);
            }
            catch
            {
                // Don't let a transient inner failure poison the cache for the
                // full TTL; evict and let the next caller retry from scratch.
                _cache.TryRemove(KeyValuePair.Create(keyId, entry));
                throw;
            }
        }
    }

    private async Task<byte[]> FetchAsync(string keyId, CancellationToken cancellationToken)
    {
        return await _inner.GetKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
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
}
