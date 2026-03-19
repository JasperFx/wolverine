using Shouldly;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class sent_and_received_metrics_tracking
{
    [Fact]
    public void record_sent_increments_per_tenant_tracking_sent()
    {
        var tracking = new PerTenantTracking("t1");
        tracking.Sent.ShouldBe(0);

        var record = new RecordSent("t1", "ServiceA");
        record.Apply(tracking);

        tracking.Sent.ShouldBe(1);

        record.Apply(tracking);
        record.Apply(tracking);

        tracking.Sent.ShouldBe(3);
    }

    [Fact]
    public void record_received_increments_per_tenant_tracking_received()
    {
        var tracking = new PerTenantTracking("t1");
        tracking.Received.ShouldBe(0);

        var record = new RecordReceived("t1", "ServiceB");
        record.Apply(tracking);

        tracking.Received.ShouldBe(1);

        record.Apply(tracking);
        record.Apply(tracking);

        tracking.Received.ShouldBe(3);
    }

    [Fact]
    public void compile_and_reset_includes_sent_and_received_then_resets()
    {
        var tracking = new PerTenantTracking("t1");
        tracking.Sent = 5;
        tracking.Received = 7;

        var metrics = tracking.CompileAndReset();

        metrics.TenantId.ShouldBe("t1");
        metrics.Sent.ShouldBe(5);
        metrics.Received.ShouldBe(7);

        // After compile and reset, counters should be zero
        tracking.Sent.ShouldBe(0);
        tracking.Received.ShouldBe(0);
    }

    [Fact]
    public void clear_resets_sent_and_received_to_zero()
    {
        var tracking = new PerTenantTracking("t1");
        tracking.Sent = 10;
        tracking.Received = 20;

        tracking.Clear();

        tracking.Sent.ShouldBe(0);
        tracking.Received.ShouldBe(0);
    }

    [Fact]
    public void per_tenant_metrics_sum_aggregates_sent_and_received()
    {
        var metrics1 = new PerTenantMetrics("t1",
            new Executions(1, 100),
            new EffectiveTime(1, 50),
            Array.Empty<ExceptionCounts>(),
            Sent: 3,
            Received: 5);

        var metrics2 = new PerTenantMetrics("t1",
            new Executions(2, 200),
            new EffectiveTime(2, 100),
            Array.Empty<ExceptionCounts>(),
            Sent: 7,
            Received: 11);

        var metrics3 = new PerTenantMetrics("t1",
            new Executions(1, 50),
            new EffectiveTime(1, 25),
            Array.Empty<ExceptionCounts>(),
            Sent: 2,
            Received: 4);

        var group = new[] { metrics1, metrics2, metrics3 }
            .GroupBy(m => m.TenantId)
            .Single();

        var summed = PerTenantMetrics.Sum(group);

        summed.TenantId.ShouldBe("t1");
        summed.Sent.ShouldBe(12);
        summed.Received.ShouldBe(20);
    }

    [Fact]
    public void per_tenant_metrics_weight_multiplies_sent_and_received()
    {
        var metrics = new PerTenantMetrics("t1",
            new Executions(2, 200),
            new EffectiveTime(2, 100),
            Array.Empty<ExceptionCounts>(),
            Sent: 4,
            Received: 6);

        var weighted = metrics.Weight(3);

        weighted.TenantId.ShouldBe("t1");
        weighted.Sent.ShouldBe(12);
        weighted.Received.ShouldBe(18);
        weighted.Executions.Count.ShouldBe(6);
        weighted.Executions.TotalTime.ShouldBe(600);
    }
}
