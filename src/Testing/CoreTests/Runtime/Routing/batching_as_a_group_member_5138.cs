using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Routing;

/// <summary>
/// GH-5138. A <c>BatchMessagesOf&lt;T&gt;()</c> batch is a member of T's handler group: one more subscriber of T next to
/// its handler chains, on a local queue of its own. A T that reaches the group from outside the process is relayed to
/// every chain's queue and to the batch's queue.
/// </summary>
public class batching_as_a_group_member_5138
{
    private static async Task<IHost> hostWith(Action<WolverineOptions> configure, params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static Uri queueOf<THandler>() => new("local://" + typeof(THandler).FullNameInCode().ToLowerInvariant());

    private static readonly Type[] AllHandlers =
        [typeof(FirstGroupedHandler5138), typeof(SecondGroupedHandler5138), typeof(GroupedBatchHandler5138)];

    [Fact]
    public async Task the_batch_belongs_to_the_group_of_its_element_type()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<GroupedMessage5138>(), AllHandlers);

        var handlers = host.GetRuntime().Handlers;
        var batch = host.GetRuntime().Options.BatchDefinitions.Single();

        var group = handlers.GroupFor(typeof(GroupedMessage5138)).ShouldNotBeNull();
        group.Batch.ShouldBeSameAs(batch);
        group.Chains.Count.ShouldBe(2);

        handlers.BatchFor(typeof(GroupedMessage5138)).ShouldBeSameAs(batch);

        // The batch keeps its own queue, named as it always was, apart from the handler types' queues
        batch.LocalExecutionQueueName.ShouldBe(typeof(GroupedMessage5138).ToMessageTypeName().ToLowerInvariant() + "-batch");
    }

    [Fact]
    public async Task an_element_type_with_no_handler_chain_still_has_its_batch()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<BatchOnlyMessage5138>(),
            typeof(BatchOnlyBatchHandler5138));

        var handlers = host.GetRuntime().Handlers;
        handlers.GroupFor(typeof(BatchOnlyMessage5138)).ShouldBeNull();
        handlers.BatchFor(typeof(BatchOnlyMessage5138)).ShouldBeSameAs(host.GetRuntime().Options.BatchDefinitions.Single());
    }

    [Fact]
    public async Task a_message_from_outside_is_relayed_to_every_chain_and_the_batch()
    {
        using var host = await hostWith(opts =>
        {
            opts.BatchMessagesOf<GroupedMessage5138>();
            opts.ListenForMessagesFrom("stub://external-5138");
        }, AllHandlers);

        var runtime = host.GetRuntime();
        var external = runtime.Options.Transports.AllEndpoints().Single(x => x.Uri == new Uri("stub://external-5138"));
        var batchQueue = new Uri("local://" + runtime.Options.BatchDefinitions.Single().LocalExecutionQueueName);

        var handler = runtime.As<IExecutorFactory>().BuildFor(typeof(GroupedMessage5138), external)
            .ShouldBeOfType<Executor>().Handler
            .ShouldBeOfType<FanoutMessageHandler<GroupedMessage5138>>();

        // Straight to each member's queue, not through the message type's own queue
        handler.LocalQueueUris.ShouldBe(
            [queueOf<FirstGroupedHandler5138>(), queueOf<SecondGroupedHandler5138>(), batchQueue], ignoreOrder: true);
    }

    [Fact]
    public async Task the_batch_queue_runs_the_batch_and_each_handler_queue_its_own_chain()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<GroupedMessage5138>(), AllHandlers);

        var runtime = host.GetRuntime();
        var batchQueue = runtime.Endpoints.EndpointFor(
            new Uri("local://" + runtime.Options.BatchDefinitions.Single().LocalExecutionQueueName))!;

        runtime.As<IExecutorFactory>().BuildFor(typeof(GroupedMessage5138), batchQueue)
            .ShouldBeOfType<Executor>().Handler.ShouldBeOfType<BatchingProcessor<GroupedMessage5138>>();

        var firstQueue = runtime.Endpoints.EndpointFor(queueOf<FirstGroupedHandler5138>())!;
        var chain = runtime.As<IExecutorFactory>().BuildFor(typeof(GroupedMessage5138), firstQueue)
            .ShouldBeOfType<Executor>().Handler.ShouldBeAssignableTo<MessageHandler>()!.Chain!;
        MessageHandlerGroup.HandlerTypeOf(chain).ShouldBe(typeof(FirstGroupedHandler5138));
    }

    [Fact]
    public async Task publishing_runs_every_handler_type_once_and_the_batch()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<GroupedMessage5138>(b =>
        {
            b.BatchSize = 3;
            b.TriggerTime = 50.Milliseconds();
        }), AllHandlers);

        GroupedHandlers5138.Clear();

        var messages = new[] { new GroupedMessage5138(1), new GroupedMessage5138(2), new GroupedMessage5138(3) };

        var bus = host.MessageBus();
        foreach (var message in messages) await bus.PublishAsync(message);

        // The batch may be assembled in more than one flush, so wait for everything to land
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            int singles, batched;
            lock (GroupedHandlers5138.Singles) singles = GroupedHandlers5138.Singles.Count;
            lock (GroupedHandlers5138.Batched) batched = GroupedHandlers5138.Batched.Count;
            if (singles >= 6 && batched >= 3) break;

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Give a duplicate delivery a moment to show up before counting
        await Task.Delay(250, TestContext.Current.CancellationToken);

        lock (GroupedHandlers5138.Singles)
        {
            GroupedHandlers5138.Singles.Count(x => x.Item1 == typeof(FirstGroupedHandler5138)).ShouldBe(3);
            GroupedHandlers5138.Singles.Count(x => x.Item1 == typeof(SecondGroupedHandler5138)).ShouldBe(3);
        }

        lock (GroupedHandlers5138.Batched)
        {
            GroupedHandlers5138.Batched.Select(x => x.Id).OrderBy(x => x).ShouldBe([1, 2, 3]);
        }
    }
}

public record GroupedMessage5138(int Id);

public record BatchOnlyMessage5138(int Id);

public static class GroupedHandlers5138
{
    public static readonly List<(Type, int)> Singles = [];
    public static readonly List<GroupedMessage5138> Batched = [];

    public static void Clear()
    {
        lock (Singles) Singles.Clear();
        lock (Batched) Batched.Clear();
    }
}

// Hidden from conventional discovery so they cannot leak into every other host in this assembly
[WolverineIgnore]
public class FirstGroupedHandler5138
{
    public void Handle(GroupedMessage5138 message)
    {
        lock (GroupedHandlers5138.Singles) GroupedHandlers5138.Singles.Add((GetType(), message.Id));
    }
}

[WolverineIgnore]
public class SecondGroupedHandler5138
{
    public void Handle(GroupedMessage5138 message)
    {
        lock (GroupedHandlers5138.Singles) GroupedHandlers5138.Singles.Add((GetType(), message.Id));
    }
}

[WolverineIgnore]
public class GroupedBatchHandler5138
{
    public void Handle(GroupedMessage5138[] messages)
    {
        lock (GroupedHandlers5138.Batched) GroupedHandlers5138.Batched.AddRange(messages);
    }
}

[WolverineIgnore]
public class BatchOnlyBatchHandler5138
{
    public void Handle(BatchOnlyMessage5138[] messages)
    {
    }
}
