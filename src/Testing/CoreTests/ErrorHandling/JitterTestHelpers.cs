using Wolverine.ErrorHandling;

namespace CoreTests.ErrorHandling;

/// <summary>
/// Deterministic jitter strategy used by tests: Apply always returns baseDelay × multiplier.
/// </summary>
internal sealed class FixedMultiplierJitter : IJitterStrategy
{
    private readonly double _multiplier;

    public FixedMultiplierJitter(double multiplier) => _multiplier = multiplier;

    public TimeSpan Apply(TimeSpan baseDelay, int attempt)
        => TimeSpan.FromTicks((long)(baseDelay.Ticks * _multiplier));
}
