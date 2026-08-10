using JasperFx.Blocks;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

// GH-3899: scope GroupId partitioning to the message types that need it. Message types exempted
// from partitioned processing execute at the endpoint's normal parallelism instead of being
// serialized behind a GroupId slot, so a single dominant GroupId cannot collapse the whole
// listener to sequential processing for types that never asked for ordering.

public class exemption_rules
{
    public interface INeedsNoOrdering;

    public record ExemptByMarker(string Name) : INeedsNoOrdering;

    public record NotExempt(string Name);

    [Fact]
    public void no_exemptions_by_default()
    {
        var rules = new MessagePartitioningRules(new());

        rules.HasProcessingExemptions.ShouldBeFalse();
        rules.IsExemptFromPartitionedProcessing(typeof(NotExempt)).ShouldBeFalse();
    }

    [Fact]
    public void exempt_by_marker_interface()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ExemptFromPartitionedProcessing<INeedsNoOrdering>();

        rules.HasProcessingExemptions.ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(NotExempt)).ShouldBeFalse();
    }

    [Fact]
    public void exempt_by_concrete_type()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ExemptFromPartitionedProcessing<ExemptByMarker>();

        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(NotExempt)).ShouldBeFalse();
    }

    [Fact]
    public void exempt_by_predicate()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ExemptFromPartitionedProcessing(type => type.Name.StartsWith("Not"));

        rules.IsExemptFromPartitionedProcessing(typeof(NotExempt)).ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeFalse();
    }

    [Fact]
    public void exemptions_are_additive_and_fluent()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ExemptFromPartitionedProcessing<ExemptByMarker>()
            .ExemptFromPartitionedProcessing(type => type == typeof(NotExempt))
            .ShouldBeSameAs(rules);

        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(NotExempt)).ShouldBeTrue();
    }

    [Fact]
    public void cached_answer_is_stable_across_repeated_lookups()
    {
        var calls = 0;
        var rules = new MessagePartitioningRules(new());
        rules.ExemptFromPartitionedProcessing(type =>
        {
            calls++;
            return type == typeof(ExemptByMarker);
        });

        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeTrue();
        rules.IsExemptFromPartitionedProcessing(typeof(ExemptByMarker)).ShouldBeTrue();

        calls.ShouldBe(1);
    }
}

public class sharded_execution_block_exempt_lane
{
    public interface INeedsNoOrdering;

    public record UnorderedSample(string ServiceName) : INeedsNoOrdering;

    public record OrderedAppend(string ServiceName, int Sequence);

    private static MessagePartitioningRules rulesWithExemption()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage<OrderedAppend>(x => x.ServiceName);
        rules.ByMessage<UnorderedSample>(x => x.ServiceName);
        rules.ExemptFromPartitionedProcessing<INeedsNoOrdering>();
        return rules;
    }

    private static Envelope envelopeFor(object message)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;
        return envelope;
    }

    [Fact]
    public void no_exempt_lane_when_no_exemptions_are_registered()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage<OrderedAppend>(x => x.ServiceName);

        var block = new ShardedExecutionBlock(5, rules, (_, _) => Task.CompletedTask);
        block.HasExemptLane.ShouldBeFalse();
    }

    [Fact]
    public void builds_exempt_lane_when_exemptions_are_registered()
    {
        var block = new ShardedExecutionBlock(5, rulesWithExemption(), (_, _) => Task.CompletedTask);
        block.HasExemptLane.ShouldBeTrue();
    }

    [Fact]
    public async Task exempt_messages_are_not_blocked_behind_a_dominated_slot()
    {
        // The CritterWatch#969 field case in miniature: one GroupId dominates and its slot is
        // busy. Exempted messages carrying the SAME group key must still flow.
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exemptProcessed = 0;
        var exemptDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int exemptCount = 20;

        var block = new ShardedExecutionBlock(5, rulesWithExemption(), Block<Envelope>.DefaultBoundedCapacity,
            async (e, _) =>
            {
                if (e.Message is OrderedAppend)
                {
                    blockerEntered.TrySetResult();
                    await releaseBlocker.Task;
                }
                else
                {
                    if (Interlocked.Increment(ref exemptProcessed) == exemptCount)
                    {
                        exemptDone.TrySetResult();
                    }
                }
            });

        try
        {
            await block.PostAsync(envelopeFor(new OrderedAppend("the-one-service", 1)));
            await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            for (var i = 0; i < exemptCount; i++)
            {
                await block.PostAsync(envelopeFor(new UnorderedSample("the-one-service")));
            }

            // All exempt messages complete while the dominant group's slot is still blocked
            await exemptDone.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            exemptProcessed.ShouldBe(exemptCount);
        }
        finally
        {
            releaseBlocker.TrySetResult();
        }

        block.Complete();
        await block.WaitForCompletionAsync();
    }

    [Fact]
    public async Task exempt_messages_execute_in_parallel_at_the_configured_parallelism()
    {
        // Rendezvous barrier: all 4 handlers must be in flight simultaneously for any of them
        // to complete. Sequential (slot-style) execution would deadlock and time the test out.
        const int parallelism = 4;
        var arrivals = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var block = new ShardedExecutionBlock(5, rulesWithExemption(), Block<Envelope>.DefaultBoundedCapacity,
            async (_, _) =>
            {
                if (Interlocked.Increment(ref arrivals) == parallelism)
                {
                    allArrived.TrySetResult();
                }

                await allArrived.Task;
            }, exemptLaneParallelism: parallelism);

        for (var i = 0; i < parallelism; i++)
        {
            // Same group key on purpose — a slot would run these one at a time
            await block.PostAsync(envelopeFor(new UnorderedSample("the-one-service")));
        }

        await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        block.Complete();
        await block.WaitForCompletionAsync();
    }

    [Fact]
    public async Task non_exempt_messages_keep_strict_per_group_ordering_while_exempt_messages_interleave()
    {
        var executed = new List<int>();
        var inFlightOrdered = 0;
        var maxConcurrentOrdered = 0;
        var gate = new object();

        var block = new ShardedExecutionBlock(5, rulesWithExemption(), Block<Envelope>.DefaultBoundedCapacity,
            async (e, _) =>
            {
                if (e.Message is OrderedAppend append)
                {
                    var now = Interlocked.Increment(ref inFlightOrdered);
                    InterlockedMax(ref maxConcurrentOrdered, now);

                    await Task.Delay(5);
                    lock (gate)
                    {
                        executed.Add(append.Sequence);
                    }

                    Interlocked.Decrement(ref inFlightOrdered);
                }
                else
                {
                    await Task.Delay(1);
                }
            });

        const int count = 15;
        for (var i = 0; i < count; i++)
        {
            // Interleave: an ordered append followed by exempt noise with the same group key
            await block.PostAsync(envelopeFor(new OrderedAppend("the-one-service", i)));
            await block.PostAsync(envelopeFor(new UnorderedSample("the-one-service")));
        }

        block.Complete();
        await block.WaitForCompletionAsync();

        // The non-exempt messages for a single group stayed strictly sequential AND in posted order
        maxConcurrentOrdered.ShouldBe(1);
        executed.ShouldBe(Enumerable.Range(0, count).ToList());
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref location)))
        {
            if (Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }
}

public class exempting_types_from_partitioned_processing_end_to_end
{
    public interface INeedsNoOrdering;

    public record BlockingAppend(string ServiceName);

    public record FastSample(string ServiceName) : INeedsNoOrdering;

    public static class EndToEndHandlers
    {
        public static TaskCompletionSource BlockerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource ReleaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource BlockerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource SamplesDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static int SampleCount;
        public static int ExpectedSamples;

        public static void Reset(int expectedSamples)
        {
            BlockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BlockerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SamplesDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SampleCount = 0;
            ExpectedSamples = expectedSamples;
        }

        public static async Task Handle(BlockingAppend _)
        {
            BlockerEntered.TrySetResult();
            await ReleaseBlocker.Task;
            BlockerFinished.TrySetResult();
        }

        public static void Handle(FastSample _)
        {
            if (Interlocked.Increment(ref SampleCount) == ExpectedSamples)
            {
                SamplesDone.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task exempt_type_rides_endpoint_parallelism_while_same_group_slot_is_busy()
    {
        const int samples = 10;
        EndToEndHandlers.Reset(samples);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(EndToEndHandlers));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                // Group everything by its service identifier — the CritterWatch usage shape
                opts.MessagePartitioning.ByPropertyNamed("ServiceName");
                opts.MessagePartitioning.ExemptFromPartitionedProcessing<INeedsNoOrdering>();

                opts.LocalQueue("partitioned")
                    .PartitionProcessingByGroupId(PartitionSlots.Five);

                opts.PublishMessage<BlockingAppend>().ToLocalQueue("partitioned");
                opts.PublishMessage<FastSample>().ToLocalQueue("partitioned");
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var bus = host.MessageBus();

        try
        {
            // Occupy the dominant group's slot
            await bus.PublishAsync(new BlockingAppend("the-one-service"));
            await EndToEndHandlers.BlockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Same GroupId, but exempted from partitioned processing — must complete while
            // the slot is still occupied
            for (var i = 0; i < samples; i++)
            {
                await bus.PublishAsync(new FastSample("the-one-service"));
            }

            await EndToEndHandlers.SamplesDone.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            EndToEndHandlers.SampleCount.ShouldBe(samples);
            EndToEndHandlers.BlockerFinished.Task.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            EndToEndHandlers.ReleaseBlocker.TrySetResult();
        }

        await EndToEndHandlers.BlockerFinished.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }
}
