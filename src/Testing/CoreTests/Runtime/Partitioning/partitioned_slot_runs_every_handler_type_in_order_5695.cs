using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5695. Since 7.0 every handler type of a message type has its own chain and
/// local queue, so a partitioned topology's slot could only relay a message with several handler types to those queues,
/// and nothing kept a group's messages in order across them (#5763 refused it at startup instead). A buffered slot now
/// expands the message into one copy per handler type and runs the copies itself, one after the other in discovery
/// order, before it takes the group's next message
/// </summary>
public class partitioned_slot_runs_every_handler_type_in_order_5695
{
    private static async Task<IHost> startHost(Action<WolverineOptions> configure, params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static void localTopology<T>(WolverineOptions opts, string baseName, Func<T, string> groupBy,
        Action<LocalPartitionedMessageTopology>? configure = null)
    {
        opts.MessagePartitioning
            .ByMessage(groupBy)
            .PublishToPartitionedLocalMessaging(baseName, 3, topology =>
            {
                topology.Message<T>();
                configure?.Invoke(topology);
            });
    }

    private static string[] handlerOrder<T>(IHost host)
    {
        return host.GetRuntime().Handlers.GroupFor(typeof(T))!.Chains
            .Select(x => MessageHandlerGroup.HandlerTypeOf(x).Name).ToArray();
    }

    private static string[] expectedLog(IEnumerable<int> sequences, string[] handlers)
    {
        return sequences.SelectMany(sequence => handlers.Select(handler => $"{handler}:{sequence}")).ToArray();
    }

    [Fact]
    public async Task every_handler_type_runs_each_message_of_a_group_in_order_under_concurrency()
    {
        SlotLog5695.Reset();

        using var host = await startHost(opts => localTopology<SlotOrdered5695>(opts, "slotordered5695", x => x.GroupId),
            typeof(FirstSlotOrderedHandler), typeof(SecondSlotOrderedHandler), typeof(ThirdSlotOrderedHandler));

        var groups = new[] { "a", "b", "c", "d", "e" };
        const int perGroup = 12;

        await host.TrackActivity().Timeout(60.Seconds()).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            // One publisher per group, all groups at once, so the slots see the groups interleaved
            await Task.WhenAll(groups.Select(group => Task.Run(async () =>
            {
                var bus = host.MessageBus();
                for (var i = 1; i <= perGroup; i++)
                {
                    await bus.PublishAsync(new SlotOrdered5695(group, i));
                }
            })));
        }));

        var handlers = handlerOrder<SlotOrdered5695>(host);
        handlers.Length.ShouldBe(3);

        foreach (var group in groups)
        {
            SlotLog5695.For(group).ShouldBe(expectedLog(Enumerable.Range(1, perGroup), handlers));
        }

        SlotLog5695.Overlapped.ShouldBeFalse();
    }

    [Fact]
    public async Task each_copy_has_an_id_derived_from_the_original_and_names_its_handler_type()
    {
        SlotLog5695.Reset();

        using var host = await startHost(opts => localTopology<SlotOrdered5695>(opts, "slotcopies5695", x => x.GroupId),
            typeof(FirstSlotOrderedHandler), typeof(SecondSlotOrderedHandler), typeof(ThirdSlotOrderedHandler));

        var session = await host.TrackActivity().Timeout(15.Seconds())
            .PublishMessageAndWaitAsync(new SlotOrdered5695("copies", 1));

        var original = session.Sent.Envelopes().Single(x => !x.Headers.ContainsKey(EnvelopeConstants.PartitionedHandlerTypeKey));
        var copies = SlotLog5695.Envelopes("copies");
        copies.Length.ShouldBe(3);

        foreach (var (handler, copy) in copies)
        {
            var key = copy.Headers[EnvelopeConstants.PartitionedHandlerTypeKey]!;
            key.ShouldEndWith(handler);
            copy.ParentId.ShouldBe(original.Id.ToString());
            copy.Id.ShouldBe(PartitionedSlotExpansion.CopyIdFor(original.Id, key));
            copy.GroupId.ShouldBe("copies");

            // The copy ran on the slot the original was received at
            copy.Destination.ShouldBe(original.Destination);
        }

        copies.Select(x => x.Envelope.Id).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void copy_ids_are_deterministic_and_differ_by_handler_type()
    {
        var original = Guid.NewGuid();

        PartitionedSlotExpansion.CopyIdFor(original, "A.Handler").ShouldBe(PartitionedSlotExpansion.CopyIdFor(original, "A.Handler"));
        PartitionedSlotExpansion.CopyIdFor(original, "A.Handler").ShouldNotBe(PartitionedSlotExpansion.CopyIdFor(original, "B.Handler"));
        PartitionedSlotExpansion.CopyIdFor(original, "A.Handler").ShouldNotBe(PartitionedSlotExpansion.CopyIdFor(Guid.NewGuid(), "A.Handler"));
        PartitionedSlotExpansion.CopyIdFor(original, "A.Handler").ShouldNotBe(original);
    }

    [Fact]
    public async Task an_inline_retry_holds_the_group_until_the_copy_succeeds()
    {
        SlotLog5695.Reset();
        FirstFailingSlotHandler.FailFirstAttemptOf = 1;
        FirstFailingSlotHandler.AlwaysFail = 0;

        using var host = await startHost(opts =>
            {
                opts.OnException<SlotFailure5695>().RetryTimes(2);
                localTopology<FailingSlot5695>(opts, "slotretry5695", x => x.GroupId);
            },
            typeof(FirstFailingSlotHandler), typeof(SecondFailingSlotHandler));

        await host.TrackActivity().Timeout(15.Seconds()).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                for (var i = 1; i <= 3; i++) await c.PublishAsync(new FailingSlot5695("retried", i));
            }));

        var handlers = handlerOrder<FailingSlot5695>(host);
        var expected = new List<string> { $"{nameof(FirstFailingSlotHandler)}:1:failed" };
        expected.AddRange(expectedLog([1, 2, 3], handlers));

        // The failed first attempt is retried right away, ahead of everything else in the group
        SlotLog5695.For("retried").ShouldBe(expected.ToArray());
    }

    [Fact]
    public async Task a_dead_lettered_copy_lets_the_next_copy_and_the_next_message_run()
    {
        SlotLog5695.Reset();
        FirstFailingSlotHandler.FailFirstAttemptOf = 0;
        FirstFailingSlotHandler.AlwaysFail = 1;

        using var host = await startHost(opts =>
            {
                opts.OnException<SlotFailure5695>().MoveToErrorQueue();
                localTopology<FailingSlot5695>(opts, "slotdead5695", x => x.GroupId);
            },
            typeof(FirstFailingSlotHandler), typeof(SecondFailingSlotHandler));

        var session = await host.TrackActivity().Timeout(15.Seconds()).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                for (var i = 1; i <= 2; i++) await c.PublishAsync(new FailingSlot5695("dead", i));
            }));

        var handlers = handlerOrder<FailingSlot5695>(host);
        handlers.ShouldBe([nameof(FirstFailingSlotHandler), nameof(SecondFailingSlotHandler)]);

        // The first handler's copy of message 1 failed for good; its second handler and message 2 still ran, in order
        SlotLog5695.For("dead").ShouldBe([
            $"{nameof(FirstFailingSlotHandler)}:1:failed",
            $"{nameof(SecondFailingSlotHandler)}:1",
            $"{nameof(FirstFailingSlotHandler)}:2",
            $"{nameof(SecondFailingSlotHandler)}:2"
        ]);

        // Only the failed handler type's copy is dead-lettered, so replaying it runs just that handler type
        var deadLetter = session.MovedToErrorQueue.Envelopes().ShouldHaveSingleItem();
        deadLetter.Headers[EnvelopeConstants.PartitionedHandlerTypeKey].ShouldBe(typeof(FirstFailingSlotHandler).FullName);
        deadLetter.Message.ShouldBeOfType<FailingSlot5695>().Sequence.ShouldBe(1);
    }

    [Fact]
    public async Task a_copy_for_a_handler_type_that_does_not_run_on_the_slot_is_dead_lettered()
    {
        SlotLog5695.Reset();

        using var host = await startHost(opts => localTopology<SlotOrdered5695>(opts, "slotunknown5695", x => x.GroupId),
            typeof(FirstSlotOrderedHandler), typeof(SecondSlotOrderedHandler), typeof(ThirdSlotOrderedHandler));

        var slot = host.GetRuntime().Options.MessagePartitioning.ShardedMessageTopologies.Single().Slots[0].Uri;

        var session = await host.TrackActivity().Timeout(15.Seconds()).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(c => c.EndpointFor(slot).SendAsync(
                new SlotOrdered5695("unknown", 1),
                new DeliveryOptions().WithHeader(EnvelopeConstants.PartitionedHandlerTypeKey, "Renamed.Since.Then.Handler"))
                .AsTask()));

        var record = session.MovedToErrorQueue.RecordsInOrder().ShouldHaveSingleItem();
        var exception = record.Exception.ShouldBeOfType<UnknownPartitionedHandlerTypeException>();
        exception.HandlerType.ShouldBe("Renamed.Since.Then.Handler");
        exception.Message.ShouldContain(typeof(FirstSlotOrderedHandler).FullName!);

        SlotLog5695.For("unknown").ShouldBeEmpty();
    }

    [Fact]
    public async Task a_sticky_handler_keeps_running_on_its_own_queue_by_default()
    {
        SlotLog5695.Reset();

        using var host = await startHost(opts =>
            {
                opts.LocalQueue("stickyslot5695");
                localTopology<StickySlot5695>(opts, "stickytopology5695", x => x.GroupId);
            },
            typeof(PlainStickySlotHandler), typeof(StickyStickySlotHandler));

        await host.TrackActivity().Timeout(15.Seconds()).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
        {
            for (var i = 1; i <= 3; i++) await c.PublishAsync(new StickySlot5695("sticky", i));
        }));

        var entries = SlotLog5695.Envelopes("sticky");

        entries.Where(x => x.Handler == nameof(StickyStickySlotHandler))
            .Select(x => x.Envelope.Destination).ShouldAllBe(x => x == new Uri("local://stickyslot5695"));
        entries.Count(x => x.Handler == nameof(StickyStickySlotHandler)).ShouldBe(3);

        // The handler type that isn't sticky runs on the slot, in order
        var plain = entries.Where(x => x.Handler == nameof(PlainStickySlotHandler)).ToArray();
        plain.Select(x => x.Envelope.Message).Cast<StickySlot5695>().Select(x => x.Sequence).ShouldBe([1, 2, 3]);
        plain.ShouldAllBe(x => x.Envelope.Destination!.ToString().StartsWith("local://stickytopology5695"));
    }

    [Fact]
    public async Task a_sticky_handler_that_opts_in_runs_on_the_slot_in_order()
    {
        SlotLog5695.Reset();

        using var host = await startHost(opts =>
            {
                opts.LocalQueue("optinslot5695");
                localTopology<OptInSlot5695>(opts, "optintopology5695", x => x.GroupId);
            },
            typeof(PlainOptInSlotHandler), typeof(StickyOptInSlotHandler));

        await host.TrackActivity().Timeout(15.Seconds()).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
        {
            for (var i = 1; i <= 3; i++) await c.PublishAsync(new OptInSlot5695("optin", i));
        }));

        SlotLog5695.For("optin").ShouldBe(expectedLog([1, 2, 3], handlerOrder<OptInSlot5695>(host)));
        SlotLog5695.Envelopes("optin")
            .ShouldAllBe(x => x.Envelope.Destination!.ToString().StartsWith("local://optintopology5695"));
    }

    [Fact]
    public async Task with_two_topologies_each_runs_the_handler_types_it_declares_in_order()
    {
        // GH-5916. A topology's members are the handler types it declares when several topologies publish the type
        SlotLog5695.Reset();

        using var host = await startHost(opts =>
            {
                opts.MessagePartitioning.ByMessage<ModuleSlot5695>(x => x.GroupId);
                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("modulepair5695", 3, t =>
                {
                    t.Message<ModuleSlot5695>();
                    t.HandledBy(typeof(FirstModuleSlotHandler), typeof(SecondModuleSlotHandler));
                });
                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("modulethird5695", 3, t =>
                {
                    t.Message<ModuleSlot5695>();
                    t.HandledBy(typeof(ThirdModuleSlotHandler));
                });
            },
            typeof(FirstModuleSlotHandler), typeof(SecondModuleSlotHandler), typeof(ThirdModuleSlotHandler));

        await host.TrackActivity().Timeout(15.Seconds()).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
        {
            for (var i = 1; i <= 4; i++) await c.PublishAsync(new ModuleSlot5695("modules", i));
        }));

        var entries = SlotLog5695.Envelopes("modules");

        var pair = handlerOrder<ModuleSlot5695>(host).Where(x => x != nameof(ThirdModuleSlotHandler)).ToArray();
        entries.Where(x => x.Handler != nameof(ThirdModuleSlotHandler)).Select(x => x.Handler + ":" + ((ModuleSlot5695)x.Envelope.Message!).Sequence)
            .ShouldBe(expectedLog([1, 2, 3, 4], pair));
        entries.Where(x => x.Handler != nameof(ThirdModuleSlotHandler))
            .ShouldAllBe(x => x.Envelope.Destination!.ToString().StartsWith("local://modulepair5695"));

        // The other topology's one handler type runs once per message, right on its own slot
        var third = entries.Where(x => x.Handler == nameof(ThirdModuleSlotHandler)).ToArray();
        third.Select(x => ((ModuleSlot5695)x.Envelope.Message!).Sequence).ShouldBe([1, 2, 3, 4]);
        third.ShouldAllBe(x => x.Envelope.Destination!.ToString().StartsWith("local://modulethird5695"));
    }

    [Fact]
    public async Task a_message_type_with_one_handler_type_keeps_the_plain_executor()
    {
        using var host = await startHost(opts =>
            {
                localTopology<SlotOrdered5695>(opts, "slotexecutors5695", x => x.GroupId, t => t.Message<SingleSlot5695>());
                opts.MessagePartitioning.ByMessage<SingleSlot5695>(x => x.GroupId);
            },
            typeof(FirstSlotOrderedHandler), typeof(SecondSlotOrderedHandler), typeof(ThirdSlotOrderedHandler),
            typeof(SingleSlotHandler));

        var runtime = host.GetRuntime();
        var slot = runtime.Options.MessagePartitioning.ShardedMessageTopologies.Single().Slots[0];

        ((IExecutorFactory)runtime).BuildFor(typeof(SingleSlot5695), slot).ShouldNotBeOfType<PartitionedGroupExecutor>();
        ((IExecutorFactory)runtime).BuildFor(typeof(SlotOrdered5695), slot).ShouldBeOfType<PartitionedGroupExecutor>();
    }
}

public record SlotOrdered5695(string GroupId, int Sequence);

public record FailingSlot5695(string GroupId, int Sequence);

public record StickySlot5695(string GroupId, int Sequence);

public record OptInSlot5695(string GroupId, int Sequence);

public record ModuleSlot5695(string GroupId, int Sequence);

public record SingleSlot5695(string GroupId);

public class SlotFailure5695 : Exception;

public static class SlotLog5695
{
    private static readonly ConcurrentQueue<(string Group, string Entry, string Handler, Envelope Envelope)> _entries = new();
    private static readonly ConcurrentDictionary<string, int> _running = new();
    private static volatile bool _overlapped;

    public static bool Overlapped => _overlapped;

    public static void Reset()
    {
        _entries.Clear();
        _running.Clear();
        _overlapped = false;
    }

    public static async Task RecordAsync(string handler, string group, int sequence, Envelope envelope,
        string? suffix = null)
    {
        if (_running.AddOrUpdate(group, 1, (_, count) => count + 1) > 1) _overlapped = true;

        // Give the other lanes and publishers a chance to interleave
        await Task.Delay(Random.Shared.Next(0, 3));

        var entry = $"{handler}:{sequence}" + (suffix == null ? "" : ":" + suffix);
        _entries.Enqueue((group, entry, handler, envelope));

        _running.AddOrUpdate(group, 0, (_, count) => count - 1);
    }

    public static string[] For(string group) => _entries.Where(x => x.Group == group).Select(x => x.Entry).ToArray();

    public static (string Handler, Envelope Envelope)[] Envelopes(string group) =>
        _entries.Where(x => x.Group == group && !x.Entry.EndsWith(":failed")).Select(x => (x.Handler, x.Envelope))
            .ToArray();
}

public class FirstSlotOrderedHandler
{
    public Task Handle(SlotOrdered5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(FirstSlotOrderedHandler), message.GroupId, message.Sequence, envelope);
}

public class SecondSlotOrderedHandler
{
    public Task Handle(SlotOrdered5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(SecondSlotOrderedHandler), message.GroupId, message.Sequence, envelope);
}

public class ThirdSlotOrderedHandler
{
    public Task Handle(SlotOrdered5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(ThirdSlotOrderedHandler), message.GroupId, message.Sequence, envelope);
}

public class SingleSlotHandler
{
    public void Handle(SingleSlot5695 message)
    {
    }
}

public class FirstFailingSlotHandler
{
    public static int FailFirstAttemptOf { get; set; }
    public static int AlwaysFail { get; set; }

    public async Task Handle(FailingSlot5695 message, Envelope envelope)
    {
        if (message.Sequence == AlwaysFail || (message.Sequence == FailFirstAttemptOf && envelope.Attempts == 1))
        {
            await SlotLog5695.RecordAsync(nameof(FirstFailingSlotHandler), message.GroupId, message.Sequence, envelope,
                "failed");
            throw new SlotFailure5695();
        }

        await SlotLog5695.RecordAsync(nameof(FirstFailingSlotHandler), message.GroupId, message.Sequence, envelope);
    }
}

public class SecondFailingSlotHandler
{
    public Task Handle(FailingSlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(SecondFailingSlotHandler), message.GroupId, message.Sequence, envelope);
}

public class PlainStickySlotHandler
{
    public Task Handle(StickySlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(PlainStickySlotHandler), message.GroupId, message.Sequence, envelope);
}

[StickyHandler("stickyslot5695")]
public class StickyStickySlotHandler
{
    public Task Handle(StickySlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(StickyStickySlotHandler), message.GroupId, message.Sequence, envelope);
}

public class PlainOptInSlotHandler
{
    public Task Handle(OptInSlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(PlainOptInSlotHandler), message.GroupId, message.Sequence, envelope);
}

[StickyHandler("optinslot5695")]
[RunOnPartitionSlot]
public class StickyOptInSlotHandler
{
    public Task Handle(OptInSlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(StickyOptInSlotHandler), message.GroupId, message.Sequence, envelope);
}

public class FirstModuleSlotHandler
{
    public Task Handle(ModuleSlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(FirstModuleSlotHandler), message.GroupId, message.Sequence, envelope);
}

public class SecondModuleSlotHandler
{
    public Task Handle(ModuleSlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(SecondModuleSlotHandler), message.GroupId, message.Sequence, envelope);
}

public class ThirdModuleSlotHandler
{
    public Task Handle(ModuleSlot5695 message, Envelope envelope) =>
        SlotLog5695.RecordAsync(nameof(ThirdModuleSlotHandler), message.GroupId, message.Sequence, envelope);
}
