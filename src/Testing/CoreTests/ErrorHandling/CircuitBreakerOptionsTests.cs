using Shouldly;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class CircuitBreakerOptionsTests
{
    [Fact]
    public void validate_happy_path()
    {
        new CircuitBreakerOptions().AssertValid();
    }

    [Fact]
    public void minimum_threshold_cannot_be_negative()
    {
        Should.Throw<InvalidCircuitBreakerException>(() =>
        {
            new CircuitBreakerOptions { MinimumThreshold = -1 }.AssertValid();
        });
    }

    [Fact]
    public void failure_threshold_should_be_higher_than_0()
    {
        Should.Throw<InvalidCircuitBreakerException>(() =>
        {
            new CircuitBreakerOptions { FailurePercentageThreshold = 0 }.AssertValid();
        });
    }
}
