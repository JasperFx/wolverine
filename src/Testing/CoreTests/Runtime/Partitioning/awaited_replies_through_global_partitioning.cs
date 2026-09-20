using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// A LocalPartitionedMessageTopology stands in as the "external" topology so the partitioned shape runs in
/// process with no broker. The broker hop and cross-node ownership are covered by
/// two_node_awaited_replies_through_global_partitioning in SlowTests.
/// </summary>
public class awaited_replies_through_global_partitioning
{
    private const int SlotCount = 5;

    private static async Task<IHost> startHostAsync(Action<WolverineOptions>? configure = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                ReservationHandler.Reset();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReservationHandler));
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.MessagePartitioning.ByMessage<ReserveFunds>(x => x.AccountId);
                opts.MessagePartitioning.ByMessage<BlowUp>(x => x.AccountId);
                opts.MessagePartitioning.ByMessage<TakeYourTime>(x => x.AccountId);
                opts.MessagePartitioning.ByMessage<NobodyHandlesThis>(x => x.AccountId);

                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "reservations", SlotCount);
                    gp.SetExternalTopology(external, "reservations");
                    gp.Message<ReserveFunds>();
                    gp.Message<BlowUp>();
                    gp.Message<TakeYourTime>();
                    gp.Message<NobodyHandlesThis>();
                    gp.Message<TenantScoped>();
                });

                configure?.Invoke(opts);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ordinary_invoke_async_still_executes_inline_when_a_local_handler_exists()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        host.Services.GetRequiredService<IWolverineRuntime>()
            .FindInvoker(typeof(ReserveFunds)).ShouldBeOfType<Executor>();

        var reply = await bus.InvokeAsync<FundsReserved>(new ReserveFunds("acct-1", 10),
            TestContext.Current.CancellationToken);

        reply.Destination.ShouldBeNull("Ordinary InvokeAsync must stay inline, bypassing partition routing");
    }

    [Fact]
    public async Task invoke_through_routing_returns_a_typed_reply_from_a_partition_slot()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var reply = await bus.InvokeAsync<FundsReserved>(new ReserveFunds("acct-1", 10),
            new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken);

        reply.AccountId.ShouldBe("acct-1");
        reply.Amount.ShouldBe(10);
        reply.Destination.ShouldNotBeNull("The command should have been routed to a partition slot, not executed inline");
    }

    [Fact]
    public async Task shard_selection_matches_the_publishing_path()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        foreach (var accountId in new[] { "acct-1", "acct-2", "acct-3", "acct-4", "acct-5", "acct-6" })
        {
            var command = new ReserveFunds(accountId, 1);
            var published = bus.PreviewSubscriptions(command).Single().Destination;

            var reply = await bus.InvokeAsync<FundsReserved>(command,
                new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken);

            reply.Destination.ShouldBe(published!.ToString(),
                $"Group '{accountId}' was published to {published} but invoked against {reply.Destination}");
        }
    }

    [Fact]
    public async Task invocations_for_one_group_execute_sequentially()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var replies = await Task.WhenAll(Enumerable.Range(1, 6)
            .Select(i => bus.InvokeAsync<FundsReserved>(new ReserveFunds("one-account", i),
                new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken)));

        replies.Length.ShouldBe(6);
        replies.Select(x => x.Destination).Distinct().Count()
            .ShouldBe(1, "Every message for one group id must land on a single slot");
        ReservationHandler.MaxConcurrency.ShouldBe(1,
            "Messages sharing a group id must never execute concurrently");
    }

    [Fact]
    public async Task invocations_for_different_groups_are_not_serialized_against_each_other()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        await Task.WhenAll(Enumerable.Range(1, 12)
            .Select(i => bus.InvokeAsync<FundsReserved>(new ReserveFunds($"acct-{i}", i),
                new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken)));

        ReservationHandler.MaxConcurrency.ShouldBeGreaterThan(1,
            "Distinct group ids should still be able to execute in parallel across lanes");
    }

    [Fact]
    public async Task the_grouping_rule_stamps_the_group_id_on_the_request_envelope()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var reply = await bus.InvokeAsync<FundsReserved>(new ReserveFunds("acct-99", 5),
            new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken);

        reply.GroupIdAtSend.ShouldBe("acct-99");

        bus.PreviewSubscriptions(new ReserveFunds("acct-99", 5)).Single()
            .Headers[CaptureGroupIdAtSendAttribute.HeaderKey].ShouldBe("acct-99");
    }

    [Fact]
    public async Task explicit_delivery_options_group_id_wins_over_the_grouping_rule()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var command = new ReserveFunds("acct-1", 3);

        var reply = await bus.InvokeAsync<FundsReserved>(command,
            new DeliveryOptions { InvokeThroughRouting = true, GroupId = "tenant-7" },
            TestContext.Current.CancellationToken);

        reply.GroupIdAtSend.ShouldBe("tenant-7", "An explicit DeliveryOptions.GroupId must win over the rule");

        var expected = bus.PreviewSubscriptions(command, new DeliveryOptions { GroupId = "tenant-7" })
            .Single().Destination;
        reply.Destination.ShouldBe(expected!.ToString());
    }

    [Fact]
    public async Task shard_selection_honors_the_tenant_id_of_the_calling_context()
    {
        using var host = await startHostAsync(opts => opts.MessagePartitioning.ByTenantId());
        var bus = host.MessageBus();
        bus.TenantId = "tenant-3";

        var command = new TenantScoped("hello");
        var published = bus.PreviewSubscriptions(command, new DeliveryOptions { TenantId = "tenant-3" })
            .Single().Destination;

        var reply = await bus.InvokeAsync<FundsReserved>(command,
            new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken);

        reply.GroupIdAtSend.ShouldBe("tenant-3", "The tenant on the calling context must decide the group id");
        reply.Destination.ShouldBe(published!.ToString(),
            $"Tenant 'tenant-3' was published to {published} but invoked against {reply.Destination}");
    }

    [Fact]
    public async Task a_message_type_with_no_local_handler_is_now_routed_instead_of_failing()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.RoutingFor(typeof(NobodyHandlesThis)).Routes.Single()
            .ShouldBeAssignableTo<IMessageInvoker>();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(() =>
            bus.InvokeAsync<FundsReserved>(new NobodyHandlesThis("acct-1"),
                new DeliveryOptions { InvokeThroughRouting = true },
                TestContext.Current.CancellationToken, 10.Seconds()));

        ex.Message.ShouldContain("No known message handler");

        // Plain InvokeAsync<T> falls through to the same route when there is no local handler
        var plain = await Should.ThrowAsync<WolverineRequestReplyException>(() =>
            bus.InvokeAsync<FundsReserved>(new NobodyHandlesThis("acct-2"),
                TestContext.Current.CancellationToken, 10.Seconds()));

        plain.Message.ShouldContain("No known message handler");
    }

    [Fact]
    public async Task a_handler_failure_comes_back_as_a_request_reply_exception()
    {
        // The slot is a local queue, which MoveToErrorQueue's unsolicited-ack rule excludes by scheme
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(() =>
            bus.InvokeAsync<FundsReserved>(new BlowUp("acct-1"),
                new DeliveryOptions { InvokeThroughRouting = true },
                TestContext.Current.CancellationToken, 30.Seconds()));

        ex.Message.ShouldContain("this reservation cannot be made");
    }

    [Fact]
    public async Task a_handler_failure_does_not_start_sending_unsolicited_acks()
    {
        using var host = await startHostAsync();

        host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.EnableAutomaticFailureAcks.ShouldBeFalse();

        var tracked = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new BlowUp("acct-1"));

        tracked.Sent.MessagesOf<FailureAcknowledgement>().ShouldBeEmpty();
    }

    [Fact]
    public async Task times_out_when_the_handler_outlasts_the_timeout()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        await Should.ThrowAsync<TimeoutException>(() =>
            bus.InvokeAsync<FundsReserved>(new TakeYourTime("acct-1", 5_000),
                new DeliveryOptions { InvokeThroughRouting = true },
                TestContext.Current.CancellationToken, 500.Milliseconds()));
    }

    [Fact]
    public async Task honors_a_cancelled_caller_token()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // ReplyListener answers a cancelled caller token with TimeoutException, not OperationCanceledException
        await Should.ThrowAsync<TimeoutException>(() =>
            bus.InvokeAsync<FundsReserved>(new TakeYourTime("acct-1", 5_000),
                new DeliveryOptions { InvokeThroughRouting = true }, cancellation.Token, 30.Seconds()));
    }

    [Fact]
    public async Task refuses_to_invoke_when_remote_invocation_is_disabled()
    {
        using var host = await startHostAsync(opts => opts.EnableRemoteInvocation = false);
        var bus = host.MessageBus();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            bus.InvokeAsync<FundsReserved>(new ReserveFunds("acct-1", 1),
                new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(WolverineOptions.EnableRemoteInvocation));
    }

    [Fact]
    public async Task refuses_scheduled_delivery_rather_than_waiting_out_the_timeout()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            bus.InvokeAsync<FundsReserved>(new ReserveFunds("acct-1", 1),
                new DeliveryOptions { InvokeThroughRouting = true, ScheduleDelay = 10.Minutes() },
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(DeliveryOptions.InvokeThroughRouting));
    }

    [Fact]
    public async Task refuses_streaming_through_routing()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();

        Should.Throw<NotSupportedException>(() =>
        {
            _ = bus.StreamAsync<FundsReserved>(new ReserveFunds("acct-1", 1),
                new DeliveryOptions { InvokeThroughRouting = true },
                TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task refuses_a_native_ack_topology()
    {
        // Built by hand: a native-ack slot needs a transport that supports EndpointMode.NativeAck to bootstrap
        using var host = await startHostAsync();
        var rules = host.Services.GetRequiredService<IWolverineRuntime>().Options.MessagePartitioning;

        var route = new GlobalPartitionedRoute(new Uri("shard://stub/native"), rules, [], [], [],
            nativeAcks: true);

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            route.InvokeAsync<FundsReserved>(new ReserveFunds("acct-1", 1), (MessageBus)host.MessageBus()));

        ex.Message.ShouldContain(nameof(GlobalPartitionedMessageTopology.ProcessInParallelWithNativeAcks));
    }

    [Fact]
    public async Task refuses_a_partitioned_topology_that_is_not_global()
    {
        using var host = await startHostAsync(opts =>
            opts.MessagePartitioning.PublishToPartitionedLocalMessaging("shards", 3,
                topology => topology.Message<ShardedOnly>()));
        var bus = host.MessageBus();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            bus.InvokeAsync<FundsReserved>(new ShardedOnly("acct-1"),
                new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(ShardedMessageRoute));
    }

    [Fact]
    public async Task refuses_direct_reentry_onto_the_lane_the_handler_occupies()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();
        var rules = host.Services.GetRequiredService<IWolverineRuntime>().Options.MessagePartitioning;

        // Two DIFFERENT group ids sharing a lane: the case a guard keyed on group id equality would miss
        var (first, second) = findGroupIdsSharingALane(rules);
        first.ShouldNotBe(second);

        ReservationHandler.Nested = async (cmd, messageBus) =>
        {
            if (cmd.AccountId != first) return;
            ReservationHandler.NestedFailure = await Should.ThrowAsync<InvalidOperationException>(() =>
                messageBus.InvokeAsync<FundsReserved>(new ReserveFunds(second, 1),
                    new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 30.Seconds()));
        };

        await bus.InvokeAsync<FundsReserved>(new ReserveFunds(first, 1),
            new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken, 30.Seconds());

        ReservationHandler.NestedFailure.ShouldNotBeNull(
            "A nested invoke onto the caller's own lane should have been refused immediately");
        ReservationHandler.NestedFailure!.Message.ShouldContain("same partitioned lane");
    }

    [Fact]
    public async Task allows_a_nested_invoke_onto_a_different_lane_of_the_same_queue()
    {
        using var host = await startHostAsync();
        var bus = host.MessageBus();
        var rules = host.Services.GetRequiredService<IWolverineRuntime>().Options.MessagePartitioning;

        var (first, _) = findGroupIdsSharingALane(rules);
        var elsewhere = findGroupIdOnAnotherLaneOfTheSameSlot(rules, first);

        laneOf(elsewhere, rules).Slot.ShouldBe(laneOf(first, rules).Slot);
        laneOf(elsewhere, rules).Lane.ShouldNotBe(laneOf(first, rules).Lane);

        ReservationHandler.Nested = async (cmd, messageBus) =>
        {
            if (cmd.AccountId != first) return;
            ReservationHandler.NestedReply = await messageBus
                .InvokeAsync<FundsReserved>(new ReserveFunds(elsewhere, 2),
                    new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 30.Seconds());
        };

        await bus.InvokeAsync<FundsReserved>(new ReserveFunds(first, 1),
            new DeliveryOptions { InvokeThroughRouting = true }, TestContext.Current.CancellationToken, 30.Seconds());

        ReservationHandler.NestedReply.ShouldNotBeNull();
        ReservationHandler.NestedReply!.AccountId.ShouldBe(elsewhere);
    }

    private static (string First, string Second) findGroupIdsSharingALane(MessagePartitioningRules rules)
    {
        var byLane = candidateIds()
            .GroupBy(id => laneOf(id, rules))
            .First(g => g.Count() > 1)
            .ToArray();

        return (byLane[0], byLane[1]);
    }

    // The slot has to match: a different slot is a different queue, rejected before lanes are ever compared
    private static string findGroupIdOnAnotherLaneOfTheSameSlot(MessagePartitioningRules rules, string seed)
    {
        var target = laneOf(seed, rules);
        return candidateIds().First(id =>
        {
            var lane = laneOf(id, rules);
            return lane.Slot == target.Slot && lane.Lane != target.Lane;
        });
    }

    private static IEnumerable<string> candidateIds() =>
        Enumerable.Range(0, 500).Select(i => $"lane-probe-{i:D4}");

    private static (int Slot, int Lane) laneOf(string groupId, MessagePartitioningRules rules)
    {
        var slot = new Envelope(new ReserveFunds(groupId, 0)).SlotForSending(SlotCount, rules);
        var lane = new Envelope(new ReserveFunds(groupId, 0)).SlotForProcessing((int)PartitionSlots.Five, rules);
        return (slot, lane);
    }
}

[CaptureGroupIdAtSend]
public record ReserveFunds(string AccountId, int Amount);

public record FundsReserved(string AccountId, int Amount, string? Destination, string? GroupIdAtSend);

/// <summary>
/// Records the group id as an outgoing envelope rule: the receiver re-derives one when it shards into a lane,
/// so Envelope.GroupId seen by the handler cannot tell "stamped before dispatch" from "re-derived on receipt".
/// </summary>
public class CaptureGroupIdAtSendAttribute : ModifyEnvelopeAttribute
{
    public const string HeaderKey = "group-at-send";

    public override void Modify(Envelope envelope)
    {
        envelope.Headers[HeaderKey] = envelope.GroupId ?? "(none)";
    }
}

public record BlowUp(string AccountId);

public record TakeYourTime(string AccountId, int Milliseconds);

public record NobodyHandlesThis(string AccountId);

// No ByMessage rule of its own, so its group id can only come from ByTenantId()
[CaptureGroupIdAtSend]
public record TenantScoped(string Label);

public record ShardedOnly(string AccountId);

public static class ReservationHandler
{
    private static int _inFlight;

    public static int MaxConcurrency;
    public static Func<ReserveFunds, IMessageContext, Task>? Nested;
    public static Exception? NestedFailure;
    public static FundsReserved? NestedReply;

    public static void Reset()
    {
        _inFlight = 0;
        MaxConcurrency = 0;
        Nested = null;
        NestedFailure = null;
        NestedReply = null;
    }

    public static async Task<FundsReserved> Handle(ReserveFunds command, Envelope envelope, IMessageContext bus)
    {
        trackConcurrency(Interlocked.Increment(ref _inFlight));
        try
        {
            var nested = Nested;
            if (nested != null)
            {
                await nested(command, bus);
            }

            await Task.Delay(25);

            return new FundsReserved(command.AccountId, command.Amount, envelope.Destination?.ToString(),
                groupIdAtSend(envelope));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public static FundsReserved Handle(BlowUp command) =>
        throw new InvalidOperationException("this reservation cannot be made");

    public static async Task<FundsReserved> Handle(TakeYourTime command, Envelope envelope)
    {
        await Task.Delay(command.Milliseconds);
        return new FundsReserved(command.AccountId, 0, envelope.Destination?.ToString(), groupIdAtSend(envelope));
    }

    public static FundsReserved Handle(TenantScoped command, Envelope envelope) =>
        new(command.Label, 0, envelope.Destination?.ToString(), groupIdAtSend(envelope));

    private static string? groupIdAtSend(Envelope envelope) =>
        envelope.Headers.TryGetValue(CaptureGroupIdAtSendAttribute.HeaderKey, out var value) ? value : null;

    private static void trackConcurrency(int observed)
    {
        int current;
        while ((current = Volatile.Read(ref MaxConcurrency)) < observed)
        {
            if (Interlocked.CompareExchange(ref MaxConcurrency, observed, current) == current) return;
        }
    }
}
