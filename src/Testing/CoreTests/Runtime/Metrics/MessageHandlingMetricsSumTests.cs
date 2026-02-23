using JasperFx.Core;
using Wolverine.Runtime.Metrics;
using Xunit;

namespace CoreTests.Runtime.Metrics;

public class MessageHandlingMetricsSumTests
{
    private static readonly Uri Destination = new("tcp://localhost:5000");
    private const string MessageType = "MyApp.MyMessage";

    [Fact]
    public void sum_empty_collection_returns_zeroed_metrics()
    {
        var result = MessageHandlingMetrics.Sum(MessageType, Destination, []);

        result.MessageType.ShouldBe(MessageType);
        result.Destination.ShouldBe(Destination);
        result.PerTenant.ShouldBeEmpty();
        result.Range.From.ShouldBeNull();
        result.Range.To.ShouldBeNull();
    }

    [Fact]
    public void sum_single_element()
    {
        var from = DateTimeOffset.UtcNow.Subtract(5.Minutes());
        var to = DateTimeOffset.UtcNow;

        var metrics = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(from, to),
            [new PerTenantMetrics("tenant1",
                new Executions(10, 500),
                new EffectiveTime(8, 120.5),
                [new ExceptionCounts("System.InvalidOperationException", 2, 1)])]);

        var result = MessageHandlingMetrics.Sum(MessageType, Destination, [metrics]);

        result.Range.From.ShouldBe(from);
        result.Range.To.ShouldBe(to);
        result.PerTenant.Length.ShouldBe(1);
        result.PerTenant[0].TenantId.ShouldBe("tenant1");
        result.PerTenant[0].Executions.Count.ShouldBe(10);
        result.PerTenant[0].Executions.TotalTime.ShouldBe(500);
        result.PerTenant[0].EffectiveTime.Count.ShouldBe(8);
        result.PerTenant[0].EffectiveTime.TotalTime.ShouldBe(120.5);
        result.PerTenant[0].Exceptions.Length.ShouldBe(1);
        result.PerTenant[0].Exceptions[0].ExceptionType.ShouldBe("System.InvalidOperationException");
        result.PerTenant[0].Exceptions[0].Failures.ShouldBe(2);
        result.PerTenant[0].Exceptions[0].DeadLetters.ShouldBe(1);
    }

    [Fact]
    public void time_range_uses_earliest_from_and_latest_to()
    {
        var earliest = DateTimeOffset.UtcNow.Subtract(10.Minutes());
        var middle = DateTimeOffset.UtcNow.Subtract(5.Minutes());
        var latest = DateTimeOffset.UtcNow;

        var m1 = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(middle, latest),
            [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])]);

        var m2 = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(earliest, middle),
            [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])]);

        var result = MessageHandlingMetrics.Sum(MessageType, Destination, [m1, m2]);

        result.Range.From.ShouldBe(earliest);
        result.Range.To.ShouldBe(latest);
    }

    [Fact]
    public void time_range_handles_null_from_and_to()
    {
        var m1 = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(null, DateTimeOffset.UtcNow),
            [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])]);

        var m2 = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(null, null),
            [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])]);

        var result = MessageHandlingMetrics.Sum(MessageType, Destination, [m1, m2]);

        result.Range.From.ShouldBeNull();
        result.Range.To.ShouldBe(m1.Range.To);
    }

    [Fact]
    public void aggregates_per_tenant_metrics_by_tenant_id()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);

        var m1 = new MessageHandlingMetrics(MessageType, Destination, range,
        [
            new PerTenantMetrics("tenantA", new Executions(10, 500), new EffectiveTime(8, 100.0), []),
            new PerTenantMetrics("tenantB", new Executions(5, 200), new EffectiveTime(4, 50.0), [])
        ]);

        var m2 = new MessageHandlingMetrics(MessageType, Destination, range,
        [
            new PerTenantMetrics("tenantA", new Executions(20, 1000), new EffectiveTime(15, 300.0), []),
            new PerTenantMetrics("tenantC", new Executions(3, 100), new EffectiveTime(2, 25.0), [])
        ]);

        var result = MessageHandlingMetrics.Sum(MessageType, Destination, [m1, m2]);

        result.PerTenant.Length.ShouldBe(3);

        var tenantA = result.PerTenant.Single(t => t.TenantId == "tenantA");
        tenantA.Executions.Count.ShouldBe(30);
        tenantA.Executions.TotalTime.ShouldBe(1500);
        tenantA.EffectiveTime.Count.ShouldBe(23);
        tenantA.EffectiveTime.TotalTime.ShouldBe(400.0);

        var tenantB = result.PerTenant.Single(t => t.TenantId == "tenantB");
        tenantB.Executions.Count.ShouldBe(5);
        tenantB.Executions.TotalTime.ShouldBe(200);

        var tenantC = result.PerTenant.Single(t => t.TenantId == "tenantC");
        tenantC.Executions.Count.ShouldBe(3);
    }

    [Fact]
    public void aggregates_exception_counts_by_exception_type()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);

        var m1 = new MessageHandlingMetrics(MessageType, Destination, range,
        [
            new PerTenantMetrics("t1",
                new Executions(10, 500),
                new EffectiveTime(8, 100.0),
                [
                    new ExceptionCounts("System.InvalidOperationException", 3, 1),
                    new ExceptionCounts("System.TimeoutException", 1, 0)
                ])
        ]);

        var m2 = new MessageHandlingMetrics(MessageType, Destination, range,
        [
            new PerTenantMetrics("t1",
                new Executions(20, 1000),
                new EffectiveTime(15, 200.0),
                [
                    new ExceptionCounts("System.InvalidOperationException", 5, 2),
                    new ExceptionCounts("System.ArgumentException", 2, 1)
                ])
        ]);

        var result = MessageHandlingMetrics.Sum(MessageType, Destination, [m1, m2]);

        var tenant = result.PerTenant.Single(t => t.TenantId == "t1");
        tenant.Exceptions.Length.ShouldBe(3);

        var invalidOp = tenant.Exceptions.Single(e => e.ExceptionType == "System.InvalidOperationException");
        invalidOp.Failures.ShouldBe(8);
        invalidOp.DeadLetters.ShouldBe(3);

        var timeout = tenant.Exceptions.Single(e => e.ExceptionType == "System.TimeoutException");
        timeout.Failures.ShouldBe(1);
        timeout.DeadLetters.ShouldBe(0);

        var argEx = tenant.Exceptions.Single(e => e.ExceptionType == "System.ArgumentException");
        argEx.Failures.ShouldBe(2);
        argEx.DeadLetters.ShouldBe(1);
    }

    [Fact]
    public void sum_across_multiple_nodes()
    {
        var from1 = DateTimeOffset.UtcNow.Subtract(10.Minutes());
        var to1 = DateTimeOffset.UtcNow.Subtract(5.Minutes());
        var from2 = DateTimeOffset.UtcNow.Subtract(8.Minutes());
        var to2 = DateTimeOffset.UtcNow;

        var m1 = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(from1, to1),
            [new PerTenantMetrics("t", new Executions(100, 5000), new EffectiveTime(90, 4000.0),
                [new ExceptionCounts("System.Exception", 10, 5)])]);

        var m2 = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(from2, to2),
            [new PerTenantMetrics("t", new Executions(200, 8000), new EffectiveTime(180, 7000.0),
                [new ExceptionCounts("System.Exception", 20, 8)])]);

        var result = MessageHandlingMetrics.Sum(MessageType, Destination, [m1, m2]);

        result.Range.From.ShouldBe(from1);
        result.Range.To.ShouldBe(to2);

        var tenant = result.PerTenant.Single();
        tenant.Executions.Count.ShouldBe(300);
        tenant.Executions.TotalTime.ShouldBe(13000);
        tenant.EffectiveTime.Count.ShouldBe(270);
        tenant.EffectiveTime.TotalTime.ShouldBe(11000.0);

        var exceptions = tenant.Exceptions.Single();
        exceptions.Failures.ShouldBe(30);
        exceptions.DeadLetters.ShouldBe(13);
    }

    [Fact]
    public void sum_by_destination_groups_across_message_types()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);
        var dest1 = new Uri("tcp://localhost:5000");
        var dest2 = new Uri("tcp://localhost:6000");

        var metrics = new[]
        {
            new MessageHandlingMetrics("App.OrderPlaced", dest1, range,
                [new PerTenantMetrics("t1", new Executions(10, 500), new EffectiveTime(8, 100.0), [])]),
            new MessageHandlingMetrics("App.OrderShipped", dest1, range,
                [new PerTenantMetrics("t1", new Executions(20, 1000), new EffectiveTime(15, 200.0), [])]),
            new MessageHandlingMetrics("App.OrderPlaced", dest2, range,
                [new PerTenantMetrics("t1", new Executions(5, 250), new EffectiveTime(4, 50.0), [])])
        };

        var results = MessageHandlingMetrics.SumByDestination(metrics);

        results.Length.ShouldBe(2);

        var forDest1 = results.Single(r => r.Destination == dest1);
        forDest1.MessageType.ShouldBe("*");
        var tenant1 = forDest1.PerTenant.Single(t => t.TenantId == "t1");
        tenant1.Executions.Count.ShouldBe(30);
        tenant1.Executions.TotalTime.ShouldBe(1500);
        tenant1.EffectiveTime.Count.ShouldBe(23);
        tenant1.EffectiveTime.TotalTime.ShouldBe(300.0);

        var forDest2 = results.Single(r => r.Destination == dest2);
        forDest2.MessageType.ShouldBe("*");
        var tenant2 = forDest2.PerTenant.Single(t => t.TenantId == "t1");
        tenant2.Executions.Count.ShouldBe(5);
        tenant2.Executions.TotalTime.ShouldBe(250);
    }

    [Fact]
    public void sum_by_destination_aggregates_tenants_across_message_types()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);
        var dest = new Uri("tcp://localhost:5000");

        var metrics = new[]
        {
            new MessageHandlingMetrics("App.OrderPlaced", dest, range,
            [
                new PerTenantMetrics("tenantA", new Executions(10, 500), new EffectiveTime(8, 100.0), []),
                new PerTenantMetrics("tenantB", new Executions(5, 200), new EffectiveTime(4, 50.0), [])
            ]),
            new MessageHandlingMetrics("App.OrderShipped", dest, range,
            [
                new PerTenantMetrics("tenantA", new Executions(20, 1000), new EffectiveTime(15, 300.0), []),
                new PerTenantMetrics("tenantC", new Executions(3, 100), new EffectiveTime(2, 25.0), [])
            ])
        };

        var results = MessageHandlingMetrics.SumByDestination(metrics);

        results.Length.ShouldBe(1);
        var result = results[0];
        result.MessageType.ShouldBe("*");
        result.PerTenant.Length.ShouldBe(3);

        var tenantA = result.PerTenant.Single(t => t.TenantId == "tenantA");
        tenantA.Executions.Count.ShouldBe(30);

        var tenantB = result.PerTenant.Single(t => t.TenantId == "tenantB");
        tenantB.Executions.Count.ShouldBe(5);

        var tenantC = result.PerTenant.Single(t => t.TenantId == "tenantC");
        tenantC.Executions.Count.ShouldBe(3);
    }

    [Fact]
    public void sum_by_destination_merges_time_ranges()
    {
        var earliest = DateTimeOffset.UtcNow.Subtract(10.Minutes());
        var middle = DateTimeOffset.UtcNow.Subtract(5.Minutes());
        var latest = DateTimeOffset.UtcNow;
        var dest = new Uri("tcp://localhost:5000");

        var metrics = new[]
        {
            new MessageHandlingMetrics("App.Msg1", dest, new TimeRange(earliest, middle),
                [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])]),
            new MessageHandlingMetrics("App.Msg2", dest, new TimeRange(middle, latest),
                [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])])
        };

        var results = MessageHandlingMetrics.SumByDestination(metrics);

        results.Length.ShouldBe(1);
        results[0].Range.From.ShouldBe(earliest);
        results[0].Range.To.ShouldBe(latest);
    }

    [Fact]
    public void sum_by_message_type_groups_across_destinations()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);
        var dest1 = new Uri("tcp://localhost:5000");
        var dest2 = new Uri("tcp://localhost:6000");
        var allDestination = new Uri("all://");

        var metrics = new[]
        {
            new MessageHandlingMetrics("App.OrderPlaced", dest1, range,
                [new PerTenantMetrics("t1", new Executions(10, 500), new EffectiveTime(8, 100.0), [])]),
            new MessageHandlingMetrics("App.OrderPlaced", dest2, range,
                [new PerTenantMetrics("t1", new Executions(20, 1000), new EffectiveTime(15, 200.0), [])]),
            new MessageHandlingMetrics("App.OrderShipped", dest1, range,
                [new PerTenantMetrics("t1", new Executions(5, 250), new EffectiveTime(4, 50.0), [])])
        };

        var results = MessageHandlingMetrics.SumByMessageType(metrics);

        results.Length.ShouldBe(2);

        var forOrderPlaced = results.Single(r => r.MessageType == "App.OrderPlaced");
        forOrderPlaced.Destination.ShouldBe(allDestination);
        var tenant1 = forOrderPlaced.PerTenant.Single(t => t.TenantId == "t1");
        tenant1.Executions.Count.ShouldBe(30);
        tenant1.Executions.TotalTime.ShouldBe(1500);
        tenant1.EffectiveTime.Count.ShouldBe(23);
        tenant1.EffectiveTime.TotalTime.ShouldBe(300.0);

        var forOrderShipped = results.Single(r => r.MessageType == "App.OrderShipped");
        forOrderShipped.Destination.ShouldBe(allDestination);
        var tenant2 = forOrderShipped.PerTenant.Single(t => t.TenantId == "t1");
        tenant2.Executions.Count.ShouldBe(5);
        tenant2.Executions.TotalTime.ShouldBe(250);
    }

    [Fact]
    public void sum_by_message_type_aggregates_tenants_across_destinations()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);
        var dest1 = new Uri("tcp://localhost:5000");
        var dest2 = new Uri("tcp://localhost:6000");
        var allDestination = new Uri("all://");

        var metrics = new[]
        {
            new MessageHandlingMetrics("App.OrderPlaced", dest1, range,
            [
                new PerTenantMetrics("tenantA", new Executions(10, 500), new EffectiveTime(8, 100.0), []),
                new PerTenantMetrics("tenantB", new Executions(5, 200), new EffectiveTime(4, 50.0), [])
            ]),
            new MessageHandlingMetrics("App.OrderPlaced", dest2, range,
            [
                new PerTenantMetrics("tenantA", new Executions(20, 1000), new EffectiveTime(15, 300.0), []),
                new PerTenantMetrics("tenantC", new Executions(3, 100), new EffectiveTime(2, 25.0), [])
            ])
        };

        var results = MessageHandlingMetrics.SumByMessageType(metrics);

        results.Length.ShouldBe(1);
        var result = results[0];
        result.Destination.ShouldBe(allDestination);
        result.PerTenant.Length.ShouldBe(3);

        var tenantA = result.PerTenant.Single(t => t.TenantId == "tenantA");
        tenantA.Executions.Count.ShouldBe(30);

        var tenantB = result.PerTenant.Single(t => t.TenantId == "tenantB");
        tenantB.Executions.Count.ShouldBe(5);

        var tenantC = result.PerTenant.Single(t => t.TenantId == "tenantC");
        tenantC.Executions.Count.ShouldBe(3);
    }

    [Fact]
    public void sum_by_message_type_merges_time_ranges()
    {
        var earliest = DateTimeOffset.UtcNow.Subtract(10.Minutes());
        var middle = DateTimeOffset.UtcNow.Subtract(5.Minutes());
        var latest = DateTimeOffset.UtcNow;
        var allDestination = new Uri("all://");

        var metrics = new[]
        {
            new MessageHandlingMetrics("App.Msg1", new Uri("tcp://localhost:5000"), new TimeRange(earliest, middle),
                [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])]),
            new MessageHandlingMetrics("App.Msg1", new Uri("tcp://localhost:6000"), new TimeRange(middle, latest),
                [new PerTenantMetrics("t", new Executions(1, 1), new EffectiveTime(1, 1.0), [])])
        };

        var results = MessageHandlingMetrics.SumByMessageType(metrics);

        results.Length.ShouldBe(1);
        results[0].Destination.ShouldBe(allDestination);
        results[0].Range.From.ShouldBe(earliest);
        results[0].Range.To.ShouldBe(latest);
    }

    [Fact]
    public void weight_of_one_returns_same_instance()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);
        var metrics = new MessageHandlingMetrics(MessageType, Destination, range,
            [new PerTenantMetrics("t1", new Executions(10, 500), new EffectiveTime(8, 100.0),
                [new ExceptionCounts("System.Exception", 2, 1)])]);

        var result = metrics.Weight(1);

        result.ShouldBeSameAs(metrics);
    }

    [Fact]
    public void weight_multiplies_all_numeric_values()
    {
        var range = new TimeRange(DateTimeOffset.UtcNow.Subtract(1.Minutes()), DateTimeOffset.UtcNow);
        var metrics = new MessageHandlingMetrics(MessageType, Destination, range,
        [
            new PerTenantMetrics("t1",
                new Executions(10, 500),
                new EffectiveTime(8, 100.0),
                [new ExceptionCounts("System.Exception", 3, 1)]),
            new PerTenantMetrics("t2",
                new Executions(5, 200),
                new EffectiveTime(4, 50.0),
                [new ExceptionCounts("System.ArgumentException", 2, 0)])
        ]);

        var result = metrics.Weight(3);

        result.MessageType.ShouldBe(MessageType);
        result.Destination.ShouldBe(Destination);
        result.Range.ShouldBe(range);
        result.PerTenant.Length.ShouldBe(2);

        var t1 = result.PerTenant.Single(t => t.TenantId == "t1");
        t1.Executions.Count.ShouldBe(30);
        t1.Executions.TotalTime.ShouldBe(1500);
        t1.EffectiveTime.Count.ShouldBe(24);
        t1.EffectiveTime.TotalTime.ShouldBe(300.0);
        t1.Exceptions.Length.ShouldBe(1);
        t1.Exceptions[0].ExceptionType.ShouldBe("System.Exception");
        t1.Exceptions[0].Failures.ShouldBe(9);
        t1.Exceptions[0].DeadLetters.ShouldBe(3);

        var t2 = result.PerTenant.Single(t => t.TenantId == "t2");
        t2.Executions.Count.ShouldBe(15);
        t2.Executions.TotalTime.ShouldBe(600);
        t2.EffectiveTime.Count.ShouldBe(12);
        t2.EffectiveTime.TotalTime.ShouldBe(150.0);
        t2.Exceptions[0].Failures.ShouldBe(6);
        t2.Exceptions[0].DeadLetters.ShouldBe(0);
    }

    [Fact]
    public void weight_throws_for_zero()
    {
        var metrics = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(null, null), []);

        Should.Throw<ArgumentOutOfRangeException>(() => metrics.Weight(0));
    }

    [Fact]
    public void weight_throws_for_negative()
    {
        var metrics = new MessageHandlingMetrics(MessageType, Destination,
            new TimeRange(null, null), []);

        Should.Throw<ArgumentOutOfRangeException>(() => metrics.Weight(-1));
    }
}
