using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.ErrorHandling;
using Wolverine.RateLimiting;
using Wolverine.Util;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    internal RateLimitSettingsRegistry RateLimits { get; } = new();

    private bool _rateLimitingConfigured;

    internal void ConfigureRateLimit(Type messageType, RateLimitSchedule schedule, string? key = null)
    {
        var rateLimitKey = key ?? messageType.ToMessageTypeName();
        RateLimits.RegisterMessageType(messageType, new RateLimitSettings(rateLimitKey, schedule));

        ensureRateLimitingConfigured();
    }

    public WolverineOptions RateLimitEndpoint(Uri endpoint, RateLimit defaultLimit,
        Action<RateLimitSchedule>? configure = null, string? key = null)
    {
        var schedule = new RateLimitSchedule(defaultLimit);
        configure?.Invoke(schedule);
        ConfigureEndpointRateLimit(endpoint, schedule, key);

        return this;
    }

    internal void ConfigureEndpointRateLimit(Uri endpoint, RateLimitSchedule schedule, string? key = null)
    {
        var rateLimitKey = key ?? endpoint.ToString();
        RateLimits.RegisterEndpoint(endpoint, new RateLimitSettings(rateLimitKey, schedule));

        ensureRateLimitingConfigured();
    }

    private void ensureRateLimitingConfigured()
    {
        if (_rateLimitingConfigured)
        {
            return;
        }

        _rateLimitingConfigured = true;

        Policies.AddMiddleware<RateLimitMiddleware>();
        this.OnException<RateLimitExceededException>()
            .CustomActionIndefinitely(async (runtime, lifecycle, ex) =>
            {
                if (lifecycle.Envelope == null)
                {
                    return;
                }

                var continuation = new RateLimitContinuationSource().Build(ex, lifecycle.Envelope);
                await continuation.ExecuteAsync(lifecycle, runtime, DateTimeOffset.UtcNow, null).ConfigureAwait(false);
            }, "Rate limit exceeded");

        Services.TryAddSingleton<IRateLimitStore, InMemoryRateLimitStore>();
        Services.TryAddSingleton<RateLimiter>();
    }
}
