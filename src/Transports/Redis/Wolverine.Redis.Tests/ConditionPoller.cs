using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

public class ConditionPoller(ITestOutputHelper output, int maxRetries, TimeSpan retryDelay)
{
    public async ValueTask WaitForAsync(string message, Func<ValueTask<bool>> condition)
    {
        var i = 0;
        while (!await condition() && i < maxRetries)
        {
            i++;
            output.WriteLine("{0} Waiting for condition: {1} (total {2}ms)",
                DateTime.UtcNow,
                message,
                i * retryDelay.TotalMilliseconds);
            await Task.Delay(retryDelay);
        }
    }

    public ValueTask WaitForAsync(string message, Func<bool> condition) =>
        WaitForAsync(message, () => ValueTask.FromResult(condition()));
}
