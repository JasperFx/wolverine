using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5920. A message type of an external sharded topology with an ordinary
/// handler AND a <c>BatchMessagesOf&lt;T&gt;()</c> batch looped forever: the slot listener relayed it to the message
/// type's own local queue and the batch's queue, and the message type's own queue re-routed it back to a slot. The
/// ordinary handler never ran. Running both correctly on the slot needs the #5695 expansion, so startup refuses it
/// </summary>
public class sharded_topology_batch_beside_an_ordinary_handler_5920
{
    [Fact]
    public async Task the_combination_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(ShardedTallySingleHandler))
                        .IncludeType(typeof(ShardedTallyBatchHandler));
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                    opts.BatchMessagesOf<ShardedTallyItem>(b => b.TriggerTime = 100.Milliseconds());

                    opts.MessagePartitioning.ByMessage<ShardedTallyItem>(x => x.GroupId);
                    opts.MessagePartitioning.PublishToShardedRabbitQueues("shardedtally5920", 2, t =>
                    {
                        t.Message<ShardedTallyItem>();
                        t.ConfigureListening(x => x.BufferedInMemory());
                    });
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(ShardedTallyItem));
        ex.Message.ShouldContain(nameof(ShardedTallySingleHandler));
        ex.Message.ShouldContain("BatchMessagesOf");
        ex.Message.ShouldContain("shardedtally5920");
    }

    [Fact]
    public async Task a_batch_with_no_ordinary_handler_still_starts_and_runs()
    {
        ShardedTallyBatchHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ShardedTallyBatchHandler));
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.BatchMessagesOf<ShardedTallyItem>(b => b.TriggerTime = 100.Milliseconds());

                opts.MessagePartitioning.ByMessage<ShardedTallyItem>(x => x.GroupId);
                opts.MessagePartitioning.PublishToShardedRabbitQueues("shardedtallybatch5920", 2, t =>
                {
                    t.Message<ShardedTallyItem>();
                    t.ConfigureListening(x => x.BufferedInMemory());
                });
            }).StartAsync(TestContext.Current.CancellationToken);

        for (var i = 1; i <= 3; i++)
        {
            await host.MessageBus().PublishAsync(new ShardedTallyItem("g", i));
        }

        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (ShardedTallyBatchHandler.Count < 3 && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        ShardedTallyBatchHandler.Count.ShouldBe(3);
    }
}

public record ShardedTallyItem(string GroupId, int Number);

public static class ShardedTallySingleHandler
{
    public static void Handle(ShardedTallyItem item)
    {
        // Only here to give ShardedTallyItem an ordinary handler
    }
}

public static class ShardedTallyBatchHandler
{
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    public static void Reset() => Volatile.Write(ref _count, 0);

    public static void Handle(ShardedTallyItem[] items) => Interlocked.Add(ref _count, items.Length);
}
