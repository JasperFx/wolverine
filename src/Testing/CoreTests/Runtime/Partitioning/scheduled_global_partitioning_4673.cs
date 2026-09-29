using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// GH-4673. The local shortcut hands a message straight to the companion local queue when this node owns
/// the slot, skipping the broker. That is safe for a message handled NOW and wrong for one handled LATER:
/// slot ownership at send time says nothing about who owns the slot when the message comes due.
///
/// <para>Taking the shortcut for a scheduled message parks it in the inbox at the companion local queue's
/// address — and that address is live on EVERY node by design (<c>LocalQueue.IsSingleNodeListener</c> is
/// deliberately false, GH-3856). So when it came due, whichever node's scheduled poller won the advisory
/// lock executed it, concurrently with the real slot owner and under the same group id. Two nodes running
/// one group at once is the single thing global partitioning exists to prevent.</para>
/// </summary>
public class scheduled_global_partitioning_4673
{
    private static readonly StubTransport _transport = new();

    private static (GlobalPartitionedRoute Route, Envelope External, Envelope Local) routeFor()
    {
        var externalResult = new Envelope(new GlobalTestMessage("a"));
        var localResult = new Envelope(new GlobalTestMessage("a"));

        var externalSlots = new[] { Substitute.For<IMessageRoute>(), Substitute.For<IMessageRoute>() };
        var localSlots = new[] { Substitute.For<IMessageRoute>(), Substitute.For<IMessageRoute>() };

        foreach (var slot in externalSlots)
        {
            slot.CreateForSending(Arg.Any<object>(), Arg.Any<DeliveryOptions?>(), Arg.Any<ISendingAgent>(),
                Arg.Any<WolverineRuntime>(), Arg.Any<string?>()).Returns(externalResult);
        }

        foreach (var slot in localSlots)
        {
            slot.CreateForSending(Arg.Any<object>(), Arg.Any<DeliveryOptions?>(), Arg.Any<ISendingAgent>(),
                Arg.Any<WolverineRuntime>(), Arg.Any<string?>()).Returns(localResult);
        }

        // Real endpoints, so the shortcut gets past its own slot lookup and fails on the RUNTIME --
        // which is what an_immediate_message_still_consults_the_listening_agent needs to be meaningful.
        Endpoint[] externalEndpoints =
            [new StubEndpoint("slot1", _transport), new StubEndpoint("slot2", _transport)];

        var route = new GlobalPartitionedRoute(new Uri("shard://stub/4673"),
            new WolverineOptions().MessagePartitioning, externalSlots, localSlots, externalEndpoints);

        return (route, externalResult, localResult);
    }

    [Fact]
    public void a_scheduled_message_never_takes_the_local_shortcut()
    {
        var (route, external, _) = routeFor();

        // Passing a null runtime IS the assertion, the same trick GH-3709's test uses: the shortcut
        // dereferences the runtime to look up the listening agent, so this only survives if the shortcut
        // is skipped outright rather than evaluated and declined.
        var envelope = route.CreateForSending(new GlobalTestMessage("a"),
            new DeliveryOptions { ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5) }, null!, null!, null);

        envelope.ShouldBeSameAs(external);
    }

    [Fact]
    public void a_message_scheduled_with_a_delay_never_takes_the_local_shortcut()
    {
        // ScheduleDelay is the other way in, and it resolves to ScheduledTime on the envelope rather than
        // being a separate concept -- worth pinning so the two spellings cannot drift apart.
        var (route, external, _) = routeFor();

        var envelope = route.CreateForSending(new GlobalTestMessage("a"),
            new DeliveryOptions { ScheduleDelay = 5.Minutes() }, null!, null!, null);

        envelope.ShouldBeSameAs(external);
    }

    [Fact]
    public void an_immediate_message_still_consults_the_listening_agent()
    {
        // The guard against over-reading the fix: an unscheduled send must STILL evaluate the shortcut,
        // or this stops being targeted and becomes "the local shortcut is dead" -- costing every
        // in-process send a broker round trip.
        //
        // Asserting the throw is the mirror image of the trick above. The shortcut dereferences the
        // runtime, so with a null one an immediate send must fail exactly where the scheduled sends
        // sail past. Without this, both tests above would still pass if the shortcut were deleted
        // outright, and they would be proving nothing.
        var (route, _, _) = routeFor();

        Should.Throw<NullReferenceException>(() =>
            route.CreateForSending(new GlobalTestMessage("a"), null, null!, null!, null));
    }
}
