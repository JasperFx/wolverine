using JasperFx.Core;

namespace Wolverine.RateLimiting;

public readonly record struct RateLimit(int Permits, TimeSpan Window)
{
    public static RateLimit PerSecond(int permits) => new(permits, 1.Seconds());
    public static RateLimit PerMinute(int permits) => new(permits, 1.Minutes());
    public static RateLimit PerHour(int permits) => new(permits, 1.Hours());
}

public sealed record RateLimitWindow(TimeOnly Start, TimeOnly End, RateLimit Limit)
{
    public bool Matches(TimeOnly time)
    {
        if (Start == End)
        {
            return true;
        }

        if (Start < End)
        {
            return time >= Start && time < End;
        }

        return time >= Start || time < End;
    }
}

public sealed class RateLimitSchedule
{
    private readonly List<RateLimitWindow> _windows = [];

    public RateLimitSchedule(RateLimit defaultLimit)
    {
        DefaultLimit = defaultLimit;
    }

    public RateLimit DefaultLimit { get; }

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    public IReadOnlyList<RateLimitWindow> Windows => _windows;

    public RateLimitSchedule AddWindow(TimeOnly start, TimeOnly end, RateLimit limit)
    {
        _windows.Add(new RateLimitWindow(start, end, limit));
        return this;
    }

    public RateLimit Resolve(DateTimeOffset now)
    {
        validate(DefaultLimit);

        var localTime = TimeZoneInfo.ConvertTime(now, TimeZone);
        var time = TimeOnly.FromDateTime(localTime.DateTime);
        foreach (var window in _windows)
        {
            if (window.Matches(time))
            {
                validate(window.Limit);
                return window.Limit;
            }
        }

        return DefaultLimit;
    }

    private static void validate(RateLimit limit)
    {
        if (limit.Permits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit.Permits), "Rate limit permits must be greater than zero.");
        }

        if (limit.Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit.Window), "Rate limit window must be greater than zero.");
        }
    }
}

public sealed class RateLimitSettings
{
    public RateLimitSettings(string key, RateLimitSchedule schedule)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    }

    public string Key { get; }

    public RateLimitSchedule Schedule { get; }
}
