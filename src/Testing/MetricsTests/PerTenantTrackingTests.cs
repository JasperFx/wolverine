using Shouldly;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class PerTenantTrackingTests
{
    [Fact]
    public void per_tenant_clear()
    {
        var perTenant = new PerTenantTracking("t1");
        perTenant.Executions++;
        perTenant.TotalExecutionTime += 10;
        perTenant.DeadLetterCounts["foo"] = 1;
        perTenant.Failures["bar"] = 1;
        perTenant.Completions++;
        perTenant.TotalEffectiveTime = 22.4;
        
        perTenant.Clear();
        
        perTenant.Executions.ShouldBe(0);
        perTenant.TotalExecutionTime.ShouldBe(0);
        perTenant.DeadLetterCounts.Count.ShouldBe(0);
        perTenant.Failures.Count.ShouldBe(0);
        perTenant.Completions.ShouldBe(0);
        perTenant.TotalEffectiveTime.ShouldBe(0);
    }
}