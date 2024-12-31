namespace Wolverine.ErrorHandling;

public class Backoff
{
    public static IEnumerable<TimeSpan> Constant(int delay, int maxRetries)
    {
        return maxRetries == 0
            ? new List<TimeSpan>()
            : Enumerable.Range(1, maxRetries).Select(i => TimeSpan.FromMilliseconds(delay));
    }
}