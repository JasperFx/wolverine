namespace Wolverine.ErrorHandling;

internal interface IJitterStrategy
{
    TimeSpan Apply(TimeSpan baseDelay, int attempt);
}

internal sealed class FullJitter : IJitterStrategy
{
    public TimeSpan Apply(TimeSpan baseDelay, int attempt)
    {
        var extraTicks = (long)(Random.Shared.NextDouble() * baseDelay.Ticks);
        return baseDelay + TimeSpan.FromTicks(extraTicks);
    }
}

internal sealed class BoundedJitter : IJitterStrategy
{
    private readonly double _percent;

    public BoundedJitter(double percent)
    {
        if (percent <= 0)
            throw new ArgumentOutOfRangeException(nameof(percent),
                "Bounded jitter percent must be greater than zero.");

        _percent = percent;
    }

    public TimeSpan Apply(TimeSpan baseDelay, int attempt)
    {
        var extraTicks = (long)(Random.Shared.NextDouble() * baseDelay.Ticks * _percent);
        return baseDelay + TimeSpan.FromTicks(extraTicks);
    }
}

internal sealed class ExponentialJitter : IJitterStrategy
{
    public TimeSpan Apply(TimeSpan baseDelay, int attempt)
    {
        var safeAttempt = Math.Max(1, attempt);
        var extraTicks = (long)(Random.Shared.NextDouble() * baseDelay.Ticks * safeAttempt * 2);
        return baseDelay + TimeSpan.FromTicks(extraTicks);
    }
}
