namespace Wolverine.Runtime.Metrics;

public interface IHandlerMetricsData
{
    string TenantId { get; }
    void Apply(PerTenantTracking tracking);
}