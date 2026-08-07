using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-3867 part 2. A batched handler and an unbatched handler for the SAME group id must not run
/// concurrently when both message types belong to a partitioned topology. Before the fix the
/// assembled batch was enqueued to its own dedicated local queue instead of the topology slot for
/// its group, so the batch raced the very handlers the topology had just sequenced.
/// </summary>
public class batching_with_partitioned_processing : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost theHost = null!;

    public batching_with_partitioned_processing(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        GroupExecutionLog.Reset();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MessagePartitioning
                    .ByMessage<IPartitionedOrderMessage>(x => x.OrderId)
                    .PublishToPartitionedLocalMessaging("gh3867orders", 4,
                        topology => { topology.MessagesImplementing<IPartitionedOrderMessage>(); });

                opts.BatchMessagesOf<OrderTouched>(batching =>
                {
                    batching.BatchSize = 5;
                    batching.TriggerTime = 100.Milliseconds();
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task batched_and_unbatched_handlers_for_a_group_id_never_overlap()
    {
        // Three group ids so we can also prove that different groups still run in parallel -- a fix
        // that just serialized everything would pass the primary assertion but be useless.
        string[] groups = ["one", "two", "three"];

        var bus = theHost.MessageBus();
        for (var i = 0; i < 12; i++)
        {
            foreach (var group in groups)
            {
                await bus.PublishAsync(new OrderTouched(group, i));
                await bus.PublishAsync(new OrderAudited(group, i));
            }
        }

        // 12 audits per group are handled individually; the batched touches arrive as a handful of
        // batches, so wait on the audits and give the trailing batches their trigger window.
        await GroupExecutionLog.WaitForAuditsAsync(groups.Length * 12, 30.Seconds());
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        foreach (var violation in GroupExecutionLog.Violations)
        {
            _output.WriteLine(violation);
        }

        GroupExecutionLog.Violations.ShouldBeEmpty();

        // Sanity: the batched handler actually ran, so an empty violation list means something.
        GroupExecutionLog.BatchedItemCount.ShouldBe(groups.Length * 12);

        // And the partitioning is still buying us cross-group parallelism.
        GroupExecutionLog.SawDistinctGroupsRunConcurrently.ShouldBeTrue();
    }
}

public interface IPartitionedOrderMessage
{
    string OrderId { get; }
}

public record OrderTouched(string OrderId, int Sequence) : IPartitionedOrderMessage;

public record OrderAudited(string OrderId, int Sequence) : IPartitionedOrderMessage;

/// <summary>
/// Records which group ids are executing at any instant. Two handlers inside the same group id at
/// the same time is the bug; two different group ids at the same time is the feature.
/// </summary>
public static class GroupExecutionLog
{
    private static readonly ConcurrentDictionary<string, int> _active = new();
    private static int _auditsHandled;
    private static int _batchedItems;
    private static int _sawConcurrentGroups;

    public static ConcurrentBag<string> Violations { get; private set; } = new();
    public static int BatchedItemCount => _batchedItems;
    public static bool SawDistinctGroupsRunConcurrently => _sawConcurrentGroups > 0;

    public static void Reset()
    {
        _active.Clear();
        Violations = new ConcurrentBag<string>();
        _auditsHandled = 0;
        _batchedItems = 0;
        _sawConcurrentGroups = 0;
    }

    public static async Task ExecuteAsync(string groupId, string who)
    {
        var depth = _active.AddOrUpdate(groupId, 1, static (_, current) => current + 1);
        if (depth > 1)
        {
            Violations.Add($"group '{groupId}': {who} ran concurrently with another handler for the same group");
        }

        if (_active.Count(pair => pair.Value > 0) > 1)
        {
            Interlocked.Exchange(ref _sawConcurrentGroups, 1);
        }

        try
        {
            // Wide enough that a genuine overlap is observed rather than missed by timing luck.
            await Task.Delay(25.Milliseconds());
        }
        finally
        {
            _active.AddOrUpdate(groupId, 0, static (_, current) => current - 1);
        }
    }

    public static void CountAudit() => Interlocked.Increment(ref _auditsHandled);

    public static void CountBatchedItems(int count) => Interlocked.Add(ref _batchedItems, count);

    public static async Task WaitForAuditsAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Volatile.Read(ref _auditsHandled) >= expected) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException(
            $"Only {Volatile.Read(ref _auditsHandled)} of {expected} OrderAudited messages were handled within {timeout}");
    }
}

public static class PartitionedOrderHandler
{
    // The BATCHED handler. Note there is deliberately no Handle(OrderTouched) -- the batch owns the
    // element type.
    public static async Task Handle(OrderTouched[] touches)
    {
        GroupExecutionLog.CountBatchedItems(touches.Length);

        // Targeting a partitioned topology is only coherent if each batch belongs to exactly one
        // group, so the runtime swaps in the group-id batcher. Assert that rather than assume it --
        // otherwise the overlap check below would silently under-report on a mixed batch.
        var groupIds = touches.Select(x => x.OrderId).Distinct().ToArray();
        if (groupIds.Length > 1)
        {
            GroupExecutionLog.Violations.Add(
                $"a single batch spanned {groupIds.Length} group ids ({groupIds.Join(", ")}) and cannot be slotted");
        }

        await GroupExecutionLog.ExecuteAsync(groupIds[0], $"batch of {touches.Length} OrderTouched");
    }

    // The UNBATCHED handler for a sibling message type in the same group.
    public static async Task Handle(OrderAudited audited)
    {
        await GroupExecutionLog.ExecuteAsync(audited.OrderId, "OrderAudited");
        GroupExecutionLog.CountAudit();
    }
}
