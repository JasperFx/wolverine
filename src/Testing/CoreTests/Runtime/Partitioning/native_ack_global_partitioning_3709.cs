using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// GH-3709. <c>ProcessInParallelWithNativeAcks()</c> is what makes <see cref="EndpointMode.NativeAck"/>
/// reachable from a global partitioned topology: the default topology bridges each slot into a companion
/// local queue and executes there, which is precisely why a local queue -- with no broker delivery to
/// settle -- cannot host the mode. Opting in removes the companion topology and the bridge so each slot
/// listener settles its own deliveries.
/// </summary>
public class native_ack_global_partitioning_3709
{
    private readonly WolverineOptions _options = new();

    // buildEndpoint() runs from the PartitionedMessageTopology constructor, ahead of any derived
    // constructor body, so this cannot be an instance field.
    private static readonly StubTransport _transport = new();

    private class NativeAckCapableEndpoint(string queueName, StubTransport transport)
        : StubEndpoint(queueName, transport)
    {
        protected override bool supportsNativeAck => true;
    }

    /// <summary>Stands in for a sharded Rabbit topology until RabbitMqQueue opts in (GH-3708).</summary>
    private class NativeAckCapableTopology(WolverineOptions options, string baseName, int count)
        : PartitionedMessageTopology(options, PartitionSlots.Five, baseName, count)
    {
        protected override Endpoint buildEndpoint(WolverineOptions options, string name)
        {
            return new NativeAckCapableEndpoint(name, _transport);
        }
    }

    private GlobalPartitionedMessageTopology topologyWithNativeAcks(string baseName, int slots,
        out NativeAckCapableTopology external)
    {
        var topology = new GlobalPartitionedMessageTopology(_options);
        topology.ProcessInParallelWithNativeAcks();

        external = new NativeAckCapableTopology(_options, baseName, slots);
        topology.SetExternalTopology(external, baseName);

        return topology;
    }

    [Fact]
    public void sets_native_ack_on_every_external_slot()
    {
        topologyWithNativeAcks("na-mode", 3, out var external);

        foreach (var slot in external.Slots)
        {
            slot.Mode.ShouldBe(EndpointMode.NativeAck);
        }
    }

    [Fact]
    public void creates_no_companion_local_topology()
    {
        // The default path builds one automatically in SetExternalTopology -- see
        // set_external_topology_creates_companion_local_topology_with_matching_slot_count.
        var topology = topologyWithNativeAcks("na-nolocal", 3, out _);

        topology.LocalTopology.ShouldBeNull();
        topology.UsesNativeAcks.ShouldBeTrue();
    }

    [Fact]
    public void leaves_the_bridge_unwired()
    {
        // ListeningAgent wires GlobalPartitionedReceiverBridge off exactly this property, so leaving it
        // null is what keeps the bridge out of the picture. There is no separate opt-out.
        topologyWithNativeAcks("na-bridge", 3, out var external);

        foreach (var slot in external.Slots)
        {
            slot.GlobalPartitionLocalQueueUri.ShouldBeNull();
        }
    }

    [Fact]
    public void slots_keep_their_exclusive_scope_and_group_sharding()
    {
        // The cluster-wide half of the guarantee: one consumer per slot, sharded into sequential lanes
        // by group id inside it. Neither is affected by dropping the bridge.
        topologyWithNativeAcks("na-scope", 3, out var external);

        foreach (var slot in external.Slots)
        {
            slot.ListenerScope.ShouldBe(ListenerScope.Exclusive);
            slot.GroupShardingSlotNumber.ShouldBe(PartitionSlots.Five);
        }
    }

    [Fact]
    public void assert_validity_does_not_demand_a_local_topology()
    {
        var topology = topologyWithNativeAcks("na-valid", 3, out _);
        topology.Message<GlobalTestMessage>();

        Should.NotThrow(() => topology.AssertValidity());
    }

    [Fact]
    public void assert_validity_still_demands_a_subscription_and_an_external_topology()
    {
        // Relaxing the local-topology rules must not relax the others.
        var noSubscription = topologyWithNativeAcks("na-nosub", 3, out _);
        Should.Throw<InvalidOperationException>(() => noSubscription.AssertValidity())
            .Message.ShouldContain("message type matching policy");

        var noExternal = new GlobalPartitionedMessageTopology(_options);
        noExternal.ProcessInParallelWithNativeAcks();
        noExternal.Message<GlobalTestMessage>();
        Should.Throw<InvalidOperationException>(() => noExternal.AssertValidity())
            .Message.ShouldContain("external transport topology");
    }

    [Fact]
    public void mode_native_ack_still_throws_and_names_the_supported_call()
    {
        // Mode(NativeAck) would set the mode WITHOUT removing the bridge, so it stays rejected -- the
        // guard from GH-3708 is replaced, not deleted.
        var topology = new GlobalPartitionedMessageTopology(_options);

        var ex = Should.Throw<ArgumentOutOfRangeException>(() => topology.Mode(EndpointMode.NativeAck));

        ex.Message.ShouldContain("companion local queue");
        ex.Message.ShouldContain(nameof(GlobalPartitionedMessageTopology.ProcessInParallelWithNativeAcks));
    }

    [Fact]
    public void local_queues_and_native_acks_together_is_a_configuration_error()
    {
        var topology = new GlobalPartitionedMessageTopology(_options);
        topology.ProcessInParallelWithNativeAcks();

        Should.Throw<InvalidOperationException>(() => topology.LocalQueues("na-explicit", 3))
            .Message.ShouldContain("no companion local queues");
    }

    [Fact]
    public void native_acks_after_local_queues_drops_the_local_topology()
    {
        // Order independence, matching how Mode() behaves: the last word wins rather than the config
        // silently keeping queues that nothing will ever route to.
        var topology = new GlobalPartitionedMessageTopology(_options);
        topology.LocalQueues("na-first", 3);
        topology.LocalTopology.ShouldNotBeNull();

        topology.ProcessInParallelWithNativeAcks();

        topology.LocalTopology.ShouldBeNull();
    }

    [Fact]
    public void a_transport_that_has_not_opted_in_fails_fast()
    {
        // LocalPartitionedMessageTopology's slots are local queues, which never accept NativeAck.
        var topology = new GlobalPartitionedMessageTopology(_options);
        topology.ProcessInParallelWithNativeAcks();

        var external = new LocalPartitionedMessageTopology(_options, "na-unsupported", 3);

        var ex = Should.Throw<InvalidOperationException>(() => topology.SetExternalTopology(external, "na-unsupported"));
        ex.Message.ShouldContain("does not support EndpointMode.NativeAck");
    }

    [Fact]
    public void the_local_shortcut_is_disabled_so_sends_always_go_through_the_broker()
    {
        // The shortcut hands the message straight to the companion local queue when this node already
        // owns the slot. In native-ack mode the broker delivery IS the durability story, so bypassing it
        // would drop the message on a crash between send and handling.
        //
        // Passing a null runtime is the assertion: the non-native path dereferences it to look up the
        // listening agent, so this only survives if the shortcut is skipped outright.
        var externalSlots = new[] { Substitute.For<IMessageRoute>(), Substitute.For<IMessageRoute>() };
        var localSlots = Array.Empty<IMessageRoute>();
        var expected = new Envelope(new GlobalTestMessage("a"));

        foreach (var slot in externalSlots)
        {
            slot.CreateForSending(Arg.Any<object>(), Arg.Any<DeliveryOptions?>(), Arg.Any<ISendingAgent>(),
                Arg.Any<WolverineRuntime>(), Arg.Any<string?>()).Returns(expected);
        }

        var route = new GlobalPartitionedRoute(new Uri("shard://stub/na"), _options.MessagePartitioning,
            externalSlots, localSlots, [], nativeAcks: true);

        var envelope = route.CreateForSending(new GlobalTestMessage("a"), null, null!, null!, null);

        envelope.ShouldBeSameAs(expected);
    }
}
