namespace Wolverine.Runtime.Metrics;

public record RecordFailure(string ExceptionType, string TenantId) : IHandlerMetricsData
{
    public void Apply(PerTenantTracking tracking)
    {
        if (!tracking.Failures.TryAdd(ExceptionType, 1))
        {
            tracking.Failures[ExceptionType] += 1;
        }
    }
}