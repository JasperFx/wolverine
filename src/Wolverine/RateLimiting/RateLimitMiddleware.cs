using Wolverine;

namespace Wolverine.RateLimiting;

public sealed class RateLimitMiddleware
{
    private readonly RateLimiter _limiter;

    public RateLimitMiddleware(RateLimiter limiter)
    {
        _limiter = limiter;
    }

    public async Task BeforeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var check = await _limiter.CheckAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (check.Allowed)
        {
            return;
        }

        throw new RateLimitExceededException(check.Settings!.Key, check.Limit!.Value,
            check.RetryAfter!.Value);
    }
}

public class RateLimitExceededException : Exception
{
    public RateLimitExceededException(string key, RateLimit limit, TimeSpan retryAfter)
        : base($"Rate limit exceeded for '{key}'. Retry after {retryAfter.TotalSeconds:0.###} seconds.")
    {
        Key = key;
        Limit = limit;
        RetryAfter = retryAfter;
    }

    public string Key { get; }

    public RateLimit Limit { get; }

    public TimeSpan RetryAfter { get; }
}
