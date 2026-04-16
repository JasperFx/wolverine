using Shouldly;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class JitterStrategyTests
{
    [Fact]
    public void full_jitter_returns_value_between_base_and_twice_base()
    {
        var strategy = new FullJitter();
        var baseDelay = TimeSpan.FromMilliseconds(100);

        for (var i = 0; i < 10_000; i++)
        {
            var result = strategy.Apply(baseDelay, attempt: 1);
            result.ShouldBeGreaterThanOrEqualTo(baseDelay);
            result.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        }
    }

    [Fact]
    public void full_jitter_produces_non_constant_output()
    {
        var strategy = new FullJitter();
        var baseDelay = TimeSpan.FromMilliseconds(100);

        var distinct = new HashSet<long>();
        for (var i = 0; i < 1_000; i++)
        {
            distinct.Add(strategy.Apply(baseDelay, attempt: 1).Ticks);
        }

        distinct.Count.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void full_jitter_is_independent_of_attempt()
    {
        var strategy = new FullJitter();
        var baseDelay = TimeSpan.FromMilliseconds(100);

        // Verify the same range invariant holds regardless of attempt number.
        foreach (var attempt in new[] { 1, 5, 25, 100 })
        {
            for (var i = 0; i < 1_000; i++)
            {
                var result = strategy.Apply(baseDelay, attempt);
                result.ShouldBeGreaterThanOrEqualTo(baseDelay);
                result.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    [Fact]
    public void bounded_jitter_respects_percent_upper_bound()
    {
        var strategy = new BoundedJitter(0.25);
        var baseDelay = TimeSpan.FromMilliseconds(100);

        for (var i = 0; i < 10_000; i++)
        {
            var result = strategy.Apply(baseDelay, attempt: 1);
            result.ShouldBeGreaterThanOrEqualTo(baseDelay);
            result.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(125));
        }
    }

    [Fact]
    public void bounded_jitter_produces_non_constant_output()
    {
        var strategy = new BoundedJitter(0.5);
        var baseDelay = TimeSpan.FromMilliseconds(100);

        var distinct = new HashSet<long>();
        for (var i = 0; i < 1_000; i++)
        {
            distinct.Add(strategy.Apply(baseDelay, attempt: 1).Ticks);
        }

        distinct.Count.ShouldBeGreaterThan(10);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0)]
    public void bounded_jitter_rejects_non_positive_percent(double percent)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BoundedJitter(percent));
    }

    [Fact]
    public void exponential_jitter_respects_attempt_scaled_upper_bound()
    {
        var strategy = new ExponentialJitter();
        var baseDelay = TimeSpan.FromMilliseconds(100);

        for (var i = 0; i < 10_000; i++)
        {
            var result = strategy.Apply(baseDelay, attempt: 3);
            result.ShouldBeGreaterThanOrEqualTo(baseDelay);
            // Upper bound: d * (1 + 2 * attempt) = 100 * 7 = 700ms
            result.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(700));
        }
    }

    [Fact]
    public void exponential_jitter_spread_grows_with_attempt()
    {
        var strategy = new ExponentialJitter();
        var baseDelay = TimeSpan.FromMilliseconds(100);

        long maxAtAttempt1 = 0;
        long maxAtAttempt5 = 0;

        for (var i = 0; i < 2_000; i++)
        {
            maxAtAttempt1 = Math.Max(maxAtAttempt1, strategy.Apply(baseDelay, 1).Ticks);
            maxAtAttempt5 = Math.Max(maxAtAttempt5, strategy.Apply(baseDelay, 5).Ticks);
        }

        maxAtAttempt5.ShouldBeGreaterThan(maxAtAttempt1);
    }

    [Fact]
    public void exponential_jitter_produces_non_constant_output()
    {
        var strategy = new ExponentialJitter();
        var baseDelay = TimeSpan.FromMilliseconds(100);

        var distinct = new HashSet<long>();
        for (var i = 0; i < 1_000; i++)
        {
            distinct.Add(strategy.Apply(baseDelay, attempt: 2).Ticks);
        }

        distinct.Count.ShouldBeGreaterThan(10);
    }
}
