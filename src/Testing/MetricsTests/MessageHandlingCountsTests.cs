using Shouldly;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class MessageHandlingCountsTests
{
    [Fact]
    public void instrument_across_tenants()
    {
        var counts = new MessageHandlingCounts("m1", new Uri("stub://one"));
        
        counts.Increment(new RecordExecutionTime(23, "t1"));
        counts.Increment(new RecordExecutionTime(45, "t2"));
        counts.Increment(new RecordExecutionTime(55, "t1"));
        counts.Increment(new RecordExecutionTime(20, "t3"));
        counts.Increment(new RecordExecutionTime(117, "t3"));
        counts.Increment(new RecordExecutionTime(10, "t2"));
        
        counts.PerTenant["t1"].Executions.ShouldBe(2);
        counts.PerTenant["t1"].TotalExecutionTime.ShouldBe(23 + 55);
        
        counts.PerTenant["t2"].Executions.ShouldBe(2);
        counts.PerTenant["t2"].TotalExecutionTime.ShouldBe(45 + 10);
        
        counts.PerTenant["t3"].Executions.ShouldBe(2);
        counts.PerTenant["t3"].TotalExecutionTime.ShouldBe(20 + 117);
    }

    [Fact]
    public void clear()
    {
        var counts = new MessageHandlingCounts("m1", new Uri("stub://one"));
        counts.Increment(new RecordExecutionTime(23, "t1"));
        counts.Increment(new RecordExecutionTime(45, "t2"));
        counts.Increment(new RecordExecutionTime(55, "t1"));
        counts.Increment(new RecordExecutionTime(20, "t3"));
        counts.Increment(new RecordExecutionTime(117, "t3"));
        counts.Increment(new RecordExecutionTime(10, "t2"));
        counts.Clear();
        
        counts.PerTenant["t1"].Executions.ShouldBe(0);
        counts.PerTenant["t1"].TotalExecutionTime.ShouldBe(0);
        
        counts.PerTenant["t2"].Executions.ShouldBe(0);
        counts.PerTenant["t2"].TotalExecutionTime.ShouldBe(0);
        
        counts.PerTenant["t3"].Executions.ShouldBe(0);
        counts.PerTenant["t3"].TotalExecutionTime.ShouldBe(0);
    }
}