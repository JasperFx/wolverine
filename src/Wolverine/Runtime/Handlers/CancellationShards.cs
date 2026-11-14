using System.Collections.Concurrent;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// Every message a receiver executes gets its own timeout source linked to the receiver's cancellation token, and
/// linking registers a callback on that one shared token: with many threads executing messages, the shared
/// token's registration lock becomes the contention point. Each thread links instead to one of a few
/// intermediate sources, each linked to the shared token once, so cancelling the shared token still cancels every
/// in-flight message, through its intermediate.
/// </summary>
internal static class CancellationShards
{
    internal static readonly int ShardCount = Math.Max(4, Math.Min(64, Environment.ProcessorCount * 2));

    private static readonly ConcurrentDictionary<CancellationToken, Shards> _shards = new();

    /// <summary>
    /// The intermediate token for the current thread, which cancels when <paramref name="parent"/> does
    /// </summary>
    public static CancellationToken For(CancellationToken parent)
    {
        // Nothing to spread out for a token that can't be cancelled, and nothing to link to for one that already is
        if (!parent.CanBeCanceled || parent.IsCancellationRequested) return parent;

        var shards = _shards.GetOrAdd(parent, static p => new Shards(p));
        return shards.For(Environment.CurrentManagedThreadId);
    }

    internal static bool IsTracking(CancellationToken parent) => _shards.ContainsKey(parent);

    private sealed class Shards
    {
        private readonly CancellationTokenSource[] _sources;

        public Shards(CancellationToken parent)
        {
            _sources = new CancellationTokenSource[ShardCount];
            for (var i = 0; i < _sources.Length; i++)
            {
                _sources[i] = CancellationTokenSource.CreateLinkedTokenSource(parent);
            }

            // Forget the shards once the parent is cancelled, so a cancelled receiver token (a stopped listener, a
            // disposed host) doesn't keep its intermediates in the map. They are not disposed: a message still
            // linked to one may be finishing, and a cancelled linked source holds nothing worth releasing early
            parent.Register(static state =>
            {
                var (token, shards) = ((CancellationToken, Shards))state!;
                _shards.TryRemove(new KeyValuePair<CancellationToken, Shards>(token, shards));
            }, (parent, this));
        }

        public CancellationToken For(int threadId) => _sources[(uint)threadId % (uint)_sources.Length].Token;
    }
}
