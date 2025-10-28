using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class InstrumentsTests
{
    [Fact]
    public void record_failure()
    {
        var exceptionType = typeof(BadImageFormatException).FullNameInCode();
        var failure = new RecordFailure(exceptionType, "t1");

        var tracking = new PerTenantTracking("t1");
        
        failure.Apply(tracking);
        tracking.Failures[exceptionType].ShouldBe(1);
        tracking.DeadLetterCounts.ContainsKey(exceptionType).ShouldBeFalse();
    }

    [Fact]
    public void record_dead_letter()
    {
        var exceptionType = typeof(BadImageFormatException).FullNameInCode();
        var deadLetter = new RecordDeadLetter(exceptionType, "t1");

        var tracking = new PerTenantTracking("t1");
        
        deadLetter.Apply(tracking);
        tracking.DeadLetterCounts[exceptionType].ShouldBe(1);
        tracking.Failures.ContainsKey(exceptionType).ShouldBeFalse();
    }

    [Fact]
    public void record_effective_time()
    {
        var effectiveTime1 = new RecordEffectiveTime(11.2, "t1");
        var effectiveTime2 = new RecordEffectiveTime(2.1, "t1");
        var effectiveTime3 = new RecordEffectiveTime(3.5, "t1");
        
        var tracking = new PerTenantTracking("t1");
        
        effectiveTime1.Apply(tracking);
        effectiveTime2.Apply(tracking);
        effectiveTime3.Apply(tracking);
        
        tracking.TotalEffectiveTime.ShouldBe(effectiveTime1.Time + effectiveTime2.Time + effectiveTime3.Time);
        tracking.Completions.ShouldBe(3);
    }

    [Fact]
    public void record_execution_time()
    {
        var execution1 = new RecordExecutionTime(Random.Shared.Next(100, 1000), "t1");
        var execution2 = new RecordExecutionTime(Random.Shared.Next(100, 1000), "t1");
        var execution3 = new RecordExecutionTime(Random.Shared.Next(100, 1000), "t1");
        var execution4 = new RecordExecutionTime(Random.Shared.Next(100, 1000), "t1");
        
        var tracking = new PerTenantTracking("t1");
        
        execution1.Apply(tracking);
        execution2.Apply(tracking);
        execution3.Apply(tracking);
        execution4.Apply(tracking);
        
        tracking.Executions.ShouldBe(4);
        tracking.TotalExecutionTime.ShouldBe(execution1.Time + execution2.Time + execution3.Time + execution4.Time);

    }
    
}