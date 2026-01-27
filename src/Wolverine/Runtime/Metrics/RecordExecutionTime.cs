namespace Wolverine.Runtime.Metrics;

public record RecordExecutionTime(long Time, string TenantId) : IHandlerMetricsData
{
    public void Apply(PerTenantTracking tracking)
    {
        tracking.Executions++;
        tracking.TotalExecutionTime += Time;
    }
}