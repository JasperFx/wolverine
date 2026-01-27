namespace Wolverine.Runtime.Metrics;

public record RecordDeadLetter(string ExceptionType, string TenantId) : IHandlerMetricsData
{
    public void Apply(PerTenantTracking tracking)
    {
        if (!tracking.DeadLetterCounts.TryAdd(ExceptionType, 1))
        {
            tracking.DeadLetterCounts[ExceptionType] += 1;
        }
    }
}