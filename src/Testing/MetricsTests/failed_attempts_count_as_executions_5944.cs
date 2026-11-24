using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MetricsTests;

/// <summary>
/// GH-5944. In <see cref="WolverineMetricsMode.Hybrid"/>, a failed handler attempt posted a
/// <c>RecordFailure</c> but never a <c>RecordExecutionTime</c>, while the CritterWatch (Direct) tracker
/// counted the same attempt once in each. So a message retried twice before it succeeded read as
/// 2 failures against 1 execution in Hybrid mode and 2 against 3 in CritterWatch mode, and a failure
/// rate derived from the two could pass 100 %. Both modes must count every attempt as an execution.
/// </summary>
public class failed_attempts_count_as_executions_5944
{
    [Theory]
    [InlineData(WolverineMetricsMode.CritterWatch)]
    [InlineData(WolverineMetricsMode.Hybrid)]
    public async Task every_attempt_counts_as_an_execution(WolverineMetricsMode mode)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Metrics.Mode = mode)
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var destination = new Uri("rabbitmq://queue/incoming");
        var envelope = new Envelope(new RetriedMessage("one"))
        {
            MessageType = typeof(RetriedMessage).FullName!,
            Destination = destination,
            TenantId = "globex"
        };

        var accumulator = runtime.MetricsAccumulator.FindAccumulator(envelope.MessageType, destination);
        IMessageTracker tracker = mode == WolverineMetricsMode.Hybrid
            ? new WolverineRuntime.HybridMetricsPublishingMessageTracker(runtime, accumulator.EntryPoint)
            : new WolverineRuntime.DirectMetricsPublishingMessageTracker(runtime, accumulator.EntryPoint);

        // Two failed attempts, then one that succeeds -- the same calls the executor makes per attempt
        for (var i = 0; i < 2; i++)
        {
            envelope.StartTiming();
            tracker.ExecutionStarted(envelope);
            tracker.ExecutionFinished(envelope, new InvalidOperationException("boom"));
        }

        envelope.StartTiming();
        tracker.ExecutionStarted(envelope);
        tracker.ExecutionFinished(envelope);

        await accumulator.EntryPoint.WaitForCompletionAsync();
        var tenant = accumulator.TriggerExport(1).PerTenant.Single(x => x.TenantId == "globex");

        tenant.Exceptions.Sum(x => x.Failures).ShouldBe(2);
        tenant.Executions.Count.ShouldBe(3);
    }
}

public record RetriedMessage(string Name);

public static class RetriedMessageHandler
{
    public static void Handle(RetriedMessage message)
    {
    }
}
