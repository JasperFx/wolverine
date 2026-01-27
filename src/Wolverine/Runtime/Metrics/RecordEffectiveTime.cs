namespace Wolverine.Runtime.Metrics;

public record RecordEffectiveTime(double Time, string TenantId) : IHandlerMetricsData
{
    public void Apply(PerTenantTracking tracking)
    {
        tracking.Completions++;
        tracking.TotalEffectiveTime += Time;
    }
}