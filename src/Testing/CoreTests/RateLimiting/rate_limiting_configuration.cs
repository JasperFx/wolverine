using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.RateLimiting;
using Xunit;

namespace CoreTests.RateLimiting;

public class rate_limiting_configuration
{
    [Fact]
    public void schedule_selects_matching_window()
    {
        var schedule = new RateLimitSchedule(RateLimit.PerMinute(900))
        {
            TimeZone = TimeZoneInfo.Utc
        };

        schedule.AddWindow(new TimeOnly(8, 0), new TimeOnly(17, 0), RateLimit.PerMinute(400));

        schedule.Resolve(new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Permits.ShouldBe(400);

        schedule.Resolve(new DateTimeOffset(2024, 1, 1, 18, 0, 0, TimeSpan.Zero))
            .Permits.ShouldBe(900);
    }

    [Fact]
    public async Task rate_limiter_denies_after_limit()
    {
        var options = new WolverineOptions();
        options.Policies.ForMessagesOfType<RateLimitedMessage>()
            .RateLimit(RateLimit.PerMinute(2));

        var limiter = new RateLimiter(new InMemoryRateLimitStore(), options, NullLogger<RateLimiter>.Instance);
        var envelope = new Envelope(new RateLimitedMessage());
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        (await limiter.CheckAsync(envelope, now, CancellationToken.None)).Allowed.ShouldBeTrue();
        (await limiter.CheckAsync(envelope, now, CancellationToken.None)).Allowed.ShouldBeTrue();
        (await limiter.CheckAsync(envelope, now, CancellationToken.None)).Allowed.ShouldBeFalse();
    }

    private sealed record RateLimitedMessage;
}
