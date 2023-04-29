using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class BackoffTests
{
    [Fact]
    public void ConstantBackoffWithZeroRetriesShouldByEmpty()
    {
        var backoff = Backoff.Constant(10, 0);
        backoff.ShouldNotBeNull();
        backoff.Count().ShouldBe(0);
    }

    [Fact]
    public void ConstantBackoffWithNonZeroRetriesShouldYieldDelay()
    {
        const int maxRetries = 1;
        const int delay = 10;
        
        var backoff = Backoff.Constant(delay, maxRetries);
        backoff.Count().ShouldBe(maxRetries);
        backoff.First().Milliseconds.ShouldBe(delay);
    }
}