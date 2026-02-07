using System.Collections.Concurrent;

namespace Wolverine.RateLimiting;

public interface IRateLimitStore
{
    ValueTask<RateLimitStoreResult> TryAcquireAsync(RateLimitStoreRequest request,
        CancellationToken cancellationToken);
}

public sealed record RateLimitBucket(DateTimeOffset WindowStart, DateTimeOffset WindowEnd, int Limit)
{
    public static RateLimitBucket For(RateLimit limit, DateTimeOffset now)
    {
        var utcNow = now.UtcDateTime;
        var windowTicks = limit.Window.Ticks;
        var windowStartTicks = utcNow.Ticks - (utcNow.Ticks % windowTicks);
        var windowStart = new DateTimeOffset(windowStartTicks, TimeSpan.Zero);
        var windowEnd = windowStart.Add(limit.Window);

        return new RateLimitBucket(windowStart, windowEnd, limit.Permits);
    }
}

public sealed record RateLimitStoreRequest(string Key, RateLimitBucket Bucket, int Quantity, DateTimeOffset Now);

public sealed record RateLimitStoreResult(bool Allowed, int CurrentCount);

public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, BucketState> _buckets = new();

    public ValueTask<RateLimitStoreResult> TryAcquireAsync(RateLimitStoreRequest request,
        CancellationToken cancellationToken)
    {
        var bucketKey = $"{request.Key}:{request.Bucket.WindowStart.UtcTicks}";
        var state = _buckets.GetOrAdd(bucketKey, _ => new BucketState(request.Bucket.WindowEnd));
        var now = request.Now;
        bool allowed;
        int currentCount;

        lock (state.Lock)
        {
            if (state.WindowEnd <= now)
            {
                state.WindowEnd = request.Bucket.WindowEnd;
                state.Count = 0;
            }

            state.Count += request.Quantity;
            currentCount = state.Count;
            allowed = currentCount <= request.Bucket.Limit;
        }

        return new ValueTask<RateLimitStoreResult>(new RateLimitStoreResult(allowed, currentCount));
    }

    private sealed class BucketState
    {
        public BucketState(DateTimeOffset windowEnd)
        {
            WindowEnd = windowEnd;
        }

        public object Lock { get; } = new();

        public int Count { get; set; }

        public DateTimeOffset WindowEnd { get; set; }
    }
}
