using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5917. A message type of a partitioned topology that has an ordinary
/// handler AND a <c>BatchMessagesOf&lt;T&gt;()</c> batch ran the ordinary handler and never ran the batch. The
/// topology's routing is terminating, so the message never reached the batch's queue, and the slot only ran the
/// ordinary handler. Nothing was logged.
/// </summary>
public class partitioned_batch_beside_an_ordinary_handler_5917
{
    private static async Task waitForAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static void configureHandlers(WolverineOptions opts)
    {
        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType(typeof(TallySingleHandler))
            .IncludeType(typeof(TallyBatchHandler));

        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        opts.BatchMessagesOf<TallyItem>(b => b.TriggerTime = 100.Milliseconds());
    }

    private static async Task publishAndWaitForBothAsync(IHost host)
    {
        var bus = host.MessageBus();
        for (var i = 1; i <= 4; i++)
        {
            await bus.PublishAsync(new TallyItem("g" + (i % 2), i));
        }

        await waitForAsync(() => TallyLog.Singles.Count >= 4 && TallyLog.Batched.Count >= 4);

        // Give a duplicate a moment to show up before counting
        await Task.Delay(250, TestContext.Current.CancellationToken);

        TallyLog.Singles.OrderBy(x => x).ShouldBe([1, 2, 3, 4]);
        TallyLog.Batched.OrderBy(x => x).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task without_a_topology_both_the_handler_and_the_batch_run()
    {
        TallyLog.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(configureHandlers)
            .StartAsync(TestContext.Current.CancellationToken);

        await publishAndWaitForBothAsync(host);
    }

    [Fact]
    public async Task with_a_local_partitioned_topology_both_the_handler_and_the_batch_run()
    {
        TallyLog.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configureHandlers(opts);

                opts.MessagePartitioning.ByMessage<TallyItem>(x => x.GroupId)
                    .PublishToPartitionedLocalMessaging("tally", 3, t => t.Message<TallyItem>());
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await publishAndWaitForBothAsync(host);

        // Each assembled batch still runs on the topology slot for its group id (GH-3867)
        TallyLog.BatchQueues.ShouldNotBeEmpty();
        TallyLog.BatchQueues.ShouldAllBe(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^local://tally\d"));

        // ...and so does every ordinary handler execution
        TallyLog.SingleQueues.ShouldAllBe(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^local://tally\d"));
    }

    [Fact]
    public async Task with_partition_processing_by_group_id_on_a_plain_queue_both_run()
    {
        TallyLog.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configureHandlers(opts);

                opts.MessagePartitioning.ByMessage<TallyItem>(x => x.GroupId);
                opts.Policies.AllLocalQueues(q => q.PartitionProcessingByGroupId(PartitionSlots.Three));
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await publishAndWaitForBothAsync(host);
    }

    [Fact]
    public async Task a_global_partitioned_topology_refuses_the_combination_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    configureHandlers(opts);

                    opts.MessagePartitioning.ByMessage<TallyItem>(x => x.GroupId);
                    opts.MessagePartitioning.GlobalPartitioned(gp =>
                    {
                        gp.SetExternalTopology(new LocalPartitionedMessageTopology(opts, "globaltally", 3), "globaltally");
                        gp.Message<TallyItem>();
                    });
                })
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(TallyItem));
        ex.Message.ShouldContain(nameof(TallySingleHandler));
        ex.Message.ShouldContain("BatchMessagesOf");
    }

    [Fact]
    public async Task a_global_partitioned_topology_refuses_the_combination_with_two_handler_types()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    configureHandlers(opts);
                    opts.Discovery.IncludeType(typeof(TallyAuditHandler));

                    opts.MessagePartitioning.ByMessage<TallyItem>(x => x.GroupId);
                    opts.MessagePartitioning.GlobalPartitioned(gp =>
                    {
                        gp.SetExternalTopology(new LocalPartitionedMessageTopology(opts, "globaltallytwo", 3), "globaltallytwo");
                        gp.Message<TallyItem>();
                    });
                })
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(TallySingleHandler));
        ex.Message.ShouldContain(nameof(TallyAuditHandler));
    }

    [Fact]
    public async Task a_global_partitioned_topology_still_runs_a_batch_with_no_ordinary_handler()
    {
        TallyLog.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TallyBatchHandler));
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.BatchMessagesOf<TallyItem>(b => b.TriggerTime = 100.Milliseconds());

                opts.MessagePartitioning.ByMessage<TallyItem>(x => x.GroupId);
                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    gp.SetExternalTopology(new LocalPartitionedMessageTopology(opts, "globalbatchonly", 3), "globalbatchonly");
                    gp.Message<TallyItem>();
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var bus = host.MessageBus();
        for (var i = 1; i <= 4; i++)
        {
            await bus.PublishAsync(new TallyItem("g" + (i % 2), i));
        }

        await waitForAsync(() => TallyLog.Batched.Count >= 4);
        TallyLog.Batched.OrderBy(x => x).ShouldBe([1, 2, 3, 4]);
    }
}

public record TallyItem(string GroupId, int Number);

public static class TallyLog
{
    public static ConcurrentQueue<int> Singles { get; } = new();
    public static ConcurrentQueue<int> Batched { get; } = new();
    public static ConcurrentQueue<string> SingleQueues { get; } = new();
    public static ConcurrentQueue<string> BatchQueues { get; } = new();

    public static void Reset()
    {
        Singles.Clear();
        Batched.Clear();
        SingleQueues.Clear();
        BatchQueues.Clear();
    }
}

public static class TallySingleHandler
{
    public static void Handle(TallyItem item, Envelope envelope)
    {
        TallyLog.Singles.Enqueue(item.Number);
        TallyLog.SingleQueues.Enqueue(envelope.Destination?.ToString() ?? "");
    }
}

public static class TallyAuditHandler
{
    public static void Handle(TallyItem item)
    {
        // Only here to give TallyItem a second handler type
    }
}

public static class TallyBatchHandler
{
    public static void Handle(TallyItem[] items, Envelope envelope)
    {
        foreach (var item in items)
        {
            TallyLog.Batched.Enqueue(item.Number);
        }

        TallyLog.BatchQueues.Enqueue(envelope.Destination?.ToString() ?? "");
    }
}
