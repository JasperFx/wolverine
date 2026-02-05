using Microsoft.Extensions.Logging;
using Wolverine;

namespace Wolverine.RateLimiting;

public sealed class RateLimiter
{
    private readonly IRateLimitStore _store;
    private readonly RateLimitSettingsRegistry _settings;
    private readonly ILogger<RateLimiter> _logger;

    public RateLimiter(IRateLimitStore store, WolverineOptions options, ILogger<RateLimiter> logger)
    {
        _store = store;
        _settings = options.RateLimits;
        _logger = logger;
    }

    public ValueTask<RateLimitCheck> CheckAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        return CheckAsync(envelope, DateTimeOffset.UtcNow, cancellationToken);
    }

    internal async ValueTask<RateLimitCheck> CheckAsync(Envelope envelope, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!tryResolveSettings(envelope, out var settings))
        {
            return RateLimitCheck.AllowedCheck();
        }

        var limit = settings.Schedule.Resolve(now);
        var bucket = RateLimitBucket.For(limit, now);
        var result = await _store.TryAcquireAsync(
            new RateLimitStoreRequest(settings.Key, bucket, 1, now),
            cancellationToken).ConfigureAwait(false);

        if (result.Allowed)
        {
            return RateLimitCheck.AllowedCheck(settings, limit);
        }

        var retryAfter = bucket.WindowEnd - now;
        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        _logger.LogDebug("Rate limit exceeded for {Key}. Retry after {RetryAfter}", settings.Key, retryAfter);
        return RateLimitCheck.Denied(settings, limit, retryAfter);
    }

    private bool tryResolveSettings(Envelope envelope, out RateLimitSettings settings)
    {
        if (envelope.Listener != null && _settings.TryFindForEndpoint(envelope.Listener.Address, out settings))
        {
            return true;
        }

        if (envelope.Destination != null && _settings.TryFindForEndpoint(envelope.Destination, out settings))
        {
            return true;
        }

        if (envelope.Message != null && _settings.TryFindForMessageType(envelope.Message.GetType(), out settings))
        {
            return true;
        }

        settings = null!;
        return false;
    }
}

public sealed record RateLimitCheck(bool Allowed, RateLimitSettings? Settings, RateLimit? Limit,
    TimeSpan? RetryAfter)
{
    public static RateLimitCheck AllowedCheck(RateLimitSettings? settings = null, RateLimit? limit = null)
    {
        return new RateLimitCheck(true, settings, limit, null);
    }

    public static RateLimitCheck Denied(RateLimitSettings settings, RateLimit limit, TimeSpan retryAfter)
    {
        return new RateLimitCheck(false, settings, limit, retryAfter);
    }
}
