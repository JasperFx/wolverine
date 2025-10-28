using JasperFx;
using Shouldly;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class accumulating_metrics_for_a_single_message_and_destination
{
    [Fact]
    public async Task pump_in_data_for_single_tenanted()
    {
        var accumulator = new MessageTypeMetricsAccumulator("m1", new Uri("stub://one"));

        var pump = new InstrumentPump([]);
        await pump.Publish(1000, accumulator);

        var tracking = new PerTenantTracking(StorageConstants.DefaultTenantId);
        foreach (var data in pump.Data)
        {
            data.Apply(tracking);
        }

        var expected = tracking.CompileAndReset();

        await accumulator.EntryPoint.WaitForCompletionAsync();

        var dump = accumulator.TriggerExport(3);
        var actual = dump.PerTenant.Single();
        
        actual.ShouldMatch(expected);
    }
    
    [Fact]
    public async Task pump_in_data_for_multiple_tenants()
    {
        var accumulator = new MessageTypeMetricsAccumulator("m1", new Uri("stub://one"));

        var pump = new InstrumentPump(["t1", "t2", "t3", "t4"]);
        await pump.Publish(1000, "t1", accumulator);
        await pump.Publish(1000, "t2", accumulator);
        await pump.Publish(1000, "t3", accumulator);
        await pump.Publish(1000, "t4", accumulator);

        await accumulator.EntryPoint.WaitForCompletionAsync();

        var metrics = accumulator.TriggerExport(3);
        metrics.PerTenant[0].ShouldMatch(pump.GetExpectedForTenantId("t1"));
        metrics.PerTenant[1].ShouldMatch(pump.GetExpectedForTenantId("t2"));
        metrics.PerTenant[2].ShouldMatch(pump.GetExpectedForTenantId("t3"));
        metrics.PerTenant[3].ShouldMatch(pump.GetExpectedForTenantId("t4"));
    }
}

public static class TestingExtensions
{
    public static void ShouldMatch(this PerTenantMetrics actual, PerTenantMetrics expected)
    {
        actual.TenantId.ShouldBe(expected.TenantId);
        actual.Executions.ShouldBe(expected.Executions);
        actual.EffectiveTime.ShouldBe(expected.EffectiveTime);
        actual.Executions.ShouldBe(expected.Executions);
    }
}