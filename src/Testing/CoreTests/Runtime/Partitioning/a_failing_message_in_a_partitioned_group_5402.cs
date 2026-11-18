using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

// GH-5402 step 1. Pins what partitioned sequential messaging does TODAY when one message in a group fails.
// Wolverine does not park the rest of the group: once the failing message leaves the line, the next message
// for the same group id runs. These tests describe current behavior, they are not a wish list. When
// sequence-aware dead lettering lands (opt in, 7.1), these stay true for endpoints that don't opt in.
public class a_failing_message_in_a_partitioned_group_5402
{
    private static async Task<IHost> startHost(Action<WolverineOptions> errorHandling)
    {
        GroupStepHandler.Log.Clear();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(GroupStepHandler));

                opts.MessagePartitioning
                    .ByMessage<GroupStep>(x => x.GroupId)
                    .PublishToPartitionedLocalMessaging("group-steps", 2, topology =>
                    {
                        topology.MessagesImplementing<GroupStep>();
                        topology.MaxDegreeOfParallelism = PartitionSlots.Three;
                    });

                errorHandling(opts);
            }).StartAsync();
    }

    private static async Task<ITrackedSession> publishGroupsAsync(IHost host)
    {
        return await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                // "A" fails on its first message, "B" never fails
                await c.PublishAsync(new GroupStep("A", 1, Fails: true));
                await c.PublishAsync(new GroupStep("A", 2));
                await c.PublishAsync(new GroupStep("A", 3));
                await c.PublishAsync(new GroupStep("B", 1));
                await c.PublishAsync(new GroupStep("B", 2));
            }));
    }

    [Fact]
    public async Task the_next_message_in_the_group_runs_once_the_head_is_dead_lettered()
    {
        using var host = await startHost(opts =>
            opts.OnException<GroupStepFailed>().RetryTimes(2).Then.MoveToErrorQueue());

        var session = await publishGroupsAsync(host);

        // A1 went to the dead letter queue after its inline retries
        session.MovedToErrorQueue.SingleMessage<GroupStep>().ShouldBe(new GroupStep("A", 1, Fails: true));

        // ...and the rest of group A ran anyway, in order. Nothing parks A2 and A3 behind the dead letter
        GroupStepHandler.Log.Where(x => x.StartsWith("A")).ShouldBe(["A1", "A1", "A1", "A2", "A3"]);
        GroupStepHandler.Log.Where(x => x.StartsWith("B")).ShouldBe(["B1", "B2"]);

        // Inline retries hold the group: A2 only starts after A1 was dead lettered
        var records = session.AllRecordsInOrder();
        var deadLettered = records.Single(x =>
            x.MessageEventType == MessageEventType.MovedToErrorQueue && x.Message is GroupStep { GroupId: "A", Sequence: 1 });
        var a2Started = records.First(x =>
            x.MessageEventType == MessageEventType.ExecutionStarted && x.Message is GroupStep { GroupId: "A", Sequence: 2 });

        deadLettered.Sequence.ShouldBeLessThan(a2Started.Sequence);
    }

    [Fact]
    public async Task a_scheduled_retry_lets_later_messages_in_the_group_run_first()
    {
        using var host = await startHost(opts =>
            opts.OnException<GroupStepFailed>().ScheduleRetry(250.Milliseconds()).Then.MoveToErrorQueue());

        var session = await publishGroupsAsync(host);

        session.MovedToErrorQueue.SingleMessage<GroupStep>().ShouldBe(new GroupStep("A", 1, Fails: true));

        // A scheduled retry takes A1 out of line, so A2 and A3 are handled before A1's second attempt
        GroupStepHandler.Log.Where(x => x.StartsWith("A")).ShouldBe(["A1", "A2", "A3", "A1"]);
        GroupStepHandler.Log.Where(x => x.StartsWith("B")).ShouldBe(["B1", "B2"]);
    }
}

public record GroupStep(string GroupId, int Sequence, bool Fails = false);

public class GroupStepFailed(string message) : Exception(message);

public static class GroupStepHandler
{
    public static readonly ConcurrentQueue<string> Log = new();

    public static void Handle(GroupStep step)
    {
        Log.Enqueue(step.GroupId + step.Sequence);

        if (step.Fails)
        {
            throw new GroupStepFailed($"{step.GroupId}{step.Sequence} failed");
        }
    }
}
