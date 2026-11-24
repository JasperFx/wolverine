using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5695. A message type of a sharded Rabbit MQ topology with several handler
/// types is expanded on the slot listener into one copy per handler type, which run there one after the other, so the
/// messages of one group stay in order across the handler types
/// </summary>
public class sharded_topology_runs_every_handler_type_in_order_5695
{
    private static Task<IHost> startHost(string baseName, Action<WolverineOptions>? configure = null,
        Action<Wolverine.RabbitMQ.RabbitMqListenerConfiguration>? listening = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FirstShardedOrderedHandler))
                    .IncludeType(typeof(SecondShardedOrderedHandler));
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.MessagePartitioning.ByMessage<ShardedOrdered>(x => x.GroupId);
                opts.MessagePartitioning.PublishToShardedRabbitQueues(baseName, 2, t =>
                {
                    t.Message<ShardedOrdered>();
                    t.ConfigureListening(x =>
                    {
                        x.BufferedInMemory();
                        listening?.Invoke(x);
                    });
                });

                configure?.Invoke(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition() && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static string[] handlerOrder(IHost host)
    {
        return host.GetRuntime().Handlers.GroupFor(typeof(ShardedOrdered))!.Chains
            .Select(x => MessageHandlerGroup.HandlerTypeOf(x).Name).ToArray();
    }

    [Fact]
    public async Task every_handler_type_runs_each_message_of_a_group_in_order()
    {
        ShardedOrderedLog.Reset();

        using var host = await startHost("shardedordered5695");

        var groups = new[] { "a", "b", "c", "d" };
        const int perGroup = 8;

        await Task.WhenAll(groups.Select(group => Task.Run(async () =>
        {
            var bus = host.MessageBus();
            for (var i = 1; i <= perGroup; i++)
            {
                await bus.PublishAsync(new ShardedOrdered(group, i));
            }
        })));

        await waitForAsync(() => ShardedOrderedLog.Count >= groups.Length * perGroup * 2);

        var handlers = handlerOrder(host);
        foreach (var group in groups)
        {
            ShardedOrderedLog.For(group).ShouldBe(Enumerable.Range(1, perGroup)
                .SelectMany(i => handlers.Select(h => $"{h}:{i}")).ToArray());
        }

        // Every copy ran on one of the topology's Rabbit MQ queues
        ShardedOrderedLog.Destinations().ShouldAllBe(x => x.Scheme == "rabbitmq" && x.ToString().Contains("shardedordered5695"));
        ShardedOrderedLog.Overlapped.ShouldBeFalse();
    }

    [Fact]
    public async Task a_requeued_copy_comes_back_for_its_own_handler_type_only()
    {
        ShardedOrderedLog.Reset();
        FirstShardedOrderedHandler.FailFirstAttemptOf = 1;

        using var host = await startHost("shardedrequeue5695",
            opts => opts.OnException<ShardedOrderedFailure>().Requeue());

        await host.MessageBus().PublishAsync(new ShardedOrdered("requeued", 1));
        await host.MessageBus().PublishAsync(new ShardedOrdered("requeued", 2));

        await waitForAsync(() => ShardedOrderedLog.For("requeued").Length >= 4);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        var entries = ShardedOrderedLog.For("requeued");

        // The requeued copy ran again, once, in the handler type it names. The other handler type ran each message once
        entries.Count(x => x == $"{nameof(FirstShardedOrderedHandler)}:1").ShouldBe(1);
        entries.Count(x => x == $"{nameof(SecondShardedOrderedHandler)}:1").ShouldBe(1);
        entries.Count(x => x == $"{nameof(FirstShardedOrderedHandler)}:2").ShouldBe(1);
        entries.Count(x => x == $"{nameof(SecondShardedOrderedHandler)}:2").ShouldBe(1);
    }

    [Fact]
    public async Task a_durable_slot_still_refuses_a_message_type_with_several_handler_types()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await startHost("shardeddurable5695", listening: x => x.UseDurableInbox());
        });

        ex.Message.ShouldContain(nameof(ShardedOrdered));
        ex.Message.ShouldContain("Durable");
    }

    [Fact]
    public async Task a_batch_of_the_message_type_is_still_refused()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await startHost("shardedbatched5695",
                opts => opts.BatchMessagesOf<ShardedOrdered>(b => b.TriggerTime = 100.Milliseconds()));
        });

        ex.Message.ShouldContain(nameof(ShardedOrdered));
        ex.Message.ShouldContain("BatchMessagesOf");
    }
}

public record ShardedOrdered(string GroupId, int Sequence);

public class ShardedOrderedFailure : Exception;

public static class ShardedOrderedLog
{
    private static readonly ConcurrentQueue<(string Group, string Entry, Uri? Destination)> _entries = new();
    private static readonly ConcurrentDictionary<string, int> _running = new();
    private static volatile bool _overlapped;

    public static bool Overlapped => _overlapped;
    public static int Count => _entries.Count;

    public static void Reset()
    {
        _entries.Clear();
        _running.Clear();
        _overlapped = false;
        FirstShardedOrderedHandler.FailFirstAttemptOf = 0;
    }

    public static async Task RecordAsync(string handler, ShardedOrdered message, Envelope envelope)
    {
        if (_running.AddOrUpdate(message.GroupId, 1, (_, count) => count + 1) > 1) _overlapped = true;
        await Task.Delay(Random.Shared.Next(0, 3));
        _entries.Enqueue((message.GroupId, $"{handler}:{message.Sequence}", envelope.Destination));
        _running.AddOrUpdate(message.GroupId, 0, (_, count) => count - 1);
    }

    public static string[] For(string group) => _entries.Where(x => x.Group == group).Select(x => x.Entry).ToArray();

    public static Uri[] Destinations() => _entries.Select(x => x.Destination!).ToArray();
}

public class FirstShardedOrderedHandler
{
    public static int FailFirstAttemptOf { get; set; }

    public async Task Handle(ShardedOrdered message, Envelope envelope)
    {
        if (message.Sequence == FailFirstAttemptOf)
        {
            FailFirstAttemptOf = 0;
            throw new ShardedOrderedFailure();
        }

        await ShardedOrderedLog.RecordAsync(nameof(FirstShardedOrderedHandler), message, envelope);
    }
}

public class SecondShardedOrderedHandler
{
    public Task Handle(ShardedOrdered message, Envelope envelope) =>
        ShardedOrderedLog.RecordAsync(nameof(SecondShardedOrderedHandler), message, envelope);
}
