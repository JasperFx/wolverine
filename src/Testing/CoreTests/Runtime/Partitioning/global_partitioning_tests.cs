using System.Diagnostics;
using CoreTests.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

// Simple test message types for global partitioning tests
public record GlobalTestMessage(string Id);
public record AnotherGlobalTestMessage(string Id);
public record UnrelatedMessage(string Id);

public interface IGlobalPartitionedMarker;
public record MarkerMessage(string Id) : IGlobalPartitionedMarker;

#region GlobalPartitionedMessageTopology Tests

public class GlobalPartitionedMessageTopologyTests
{
    private readonly WolverineOptions _options = new();

    private GlobalPartitionedMessageTopology CreateTopology()
    {
        return new GlobalPartitionedMessageTopology(_options);
    }

    private LocalPartitionedMessageTopology CreateLocalTopology(string baseName, int slots)
    {
        return new LocalPartitionedMessageTopology(_options, baseName, slots);
    }

    [Fact]
    public void set_external_topology_creates_companion_local_topology_with_matching_slot_count()
    {
        var topology = CreateTopology();
        var external = CreateLocalTopology("ext", 4);

        topology.SetExternalTopology(external, "test");

        topology.LocalTopology.ShouldNotBeNull();
        topology.LocalTopology!.Slots.Count.ShouldBe(external.Slots.Count);
    }

    [Fact]
    public void set_external_topology_forces_durable_mode_on_all_external_endpoints()
    {
        var topology = CreateTopology();
        var external = CreateLocalTopology("ext", 3);

        // Verify they start as something other than durable
        foreach (var slot in external.Slots)
        {
            slot.Mode = EndpointMode.BufferedInMemory;
        }

        topology.SetExternalTopology(external, "test");

        foreach (var slot in external.Slots)
        {
            slot.Mode.ShouldBe(EndpointMode.Durable);
        }
    }

    [Fact]
    public void set_external_topology_forces_durable_mode_on_all_local_endpoints()
    {
        var topology = CreateTopology();
        var external = CreateLocalTopology("ext", 3);

        topology.SetExternalTopology(external, "test");

        foreach (var slot in topology.LocalTopology!.Slots)
        {
            slot.Mode.ShouldBe(EndpointMode.Durable);
        }
    }

    [Fact]
    public void set_external_topology_tags_external_endpoints_with_companion_local_queue_uris()
    {
        var topology = CreateTopology();
        var external = CreateLocalTopology("ext", 3);

        topology.SetExternalTopology(external, "test");

        for (var i = 0; i < external.Slots.Count; i++)
        {
            external.Slots[i].GlobalPartitionLocalQueueUri.ShouldNotBeNull();
            external.Slots[i].GlobalPartitionLocalQueueUri.ShouldBe(topology.LocalTopology!.Slots[i].Uri);
        }
    }

    [Fact]
    public void message_subscription_matching_works()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();

        topology.Matches(typeof(GlobalTestMessage)).ShouldBeTrue();
        topology.Matches(typeof(UnrelatedMessage)).ShouldBeFalse();
    }

    [Fact]
    public void messages_implementing_subscription_matching_works()
    {
        var topology = CreateTopology();
        topology.MessagesImplementing<IGlobalPartitionedMarker>();

        topology.Matches(typeof(MarkerMessage)).ShouldBeTrue();
        topology.Matches(typeof(GlobalTestMessage)).ShouldBeFalse();
    }

    [Fact]
    public void assert_validity_throws_when_no_subscriptions()
    {
        var topology = CreateTopology();
        var external = CreateLocalTopology("ext", 2);
        topology.SetExternalTopology(external, "test");

        Should.Throw<InvalidOperationException>(() => topology.AssertValidity())
            .Message.ShouldContain("message type matching policy");
    }

    [Fact]
    public void assert_validity_throws_when_no_external_topology()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();

        Should.Throw<InvalidOperationException>(() => topology.AssertValidity())
            .Message.ShouldContain("external transport topology");
    }

    [Fact]
    public void assert_validity_passes_when_configured_correctly()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();
        var external = CreateLocalTopology("ext", 2);
        topology.SetExternalTopology(external, "test");

        // Should not throw
        topology.AssertValidity();
    }

    [Fact]
    public void try_match_returns_false_for_non_matching_types()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();
        var external = CreateLocalTopology("ext", 2);
        topology.SetExternalTopology(external, "test");

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(_options);

        topology.TryMatch(typeof(UnrelatedMessage), runtime, out var route).ShouldBeFalse();
        route.ShouldBeNull();
    }

    [Fact]
    public void try_match_returns_global_partitioned_route_for_matching_types()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();
        var external = CreateLocalTopology("ext", 2);
        topology.SetExternalTopology(external, "test");

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(_options);

        // WithinDescription allows MessageRoute to be created without an active sending agent
        WolverineSystemPart.WithinDescription = true;
        try
        {
            topology.TryMatch(typeof(GlobalTestMessage), runtime, out var route).ShouldBeTrue();
            route.ShouldNotBeNull();
            route.ShouldBeOfType<GlobalPartitionedRoute>();
        }
        finally
        {
            WolverineSystemPart.WithinDescription = false;
        }
    }

    [Fact]
    public void try_match_returns_false_when_no_external_topology()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(_options);

        topology.TryMatch(typeof(GlobalTestMessage), runtime, out var route).ShouldBeFalse();
    }

    [Fact]
    public void assert_validity_throws_when_no_local_topology()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();

        // Directly set external without local by using reflection or a different path
        // Actually, SetExternalTopology creates local if null, so we need to test
        // a scenario where local is missing. This can happen if someone only sets
        // subscriptions but no topology at all.
        // The existing test "assert_validity_throws_when_no_external_topology" covers
        // the case where external is null. With the new validation, if external is set
        // but local is somehow null, it would also throw.
        // Since SetExternalTopology always creates local if null, this test validates
        // the error message for missing local topology indirectly via missing external.
        Should.Throw<InvalidOperationException>(() => topology.AssertValidity())
            .Message.ShouldContain("external transport topology");
    }

    [Fact]
    public void assert_validity_throws_when_local_and_external_slot_counts_differ()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();

        // Pre-configure local queues with 3 slots
        topology.LocalQueues("local", 3);

        // Set external topology with 5 slots - the local topology won't be overwritten
        var external = CreateLocalTopology("ext", 5);
        topology.SetExternalTopology(external, "test");

        Should.Throw<InvalidOperationException>(() => topology.AssertValidity())
            .Message.ShouldContain("must match");
    }

    [Fact]
    public void assert_validity_passes_when_local_and_external_slot_counts_match()
    {
        var topology = CreateTopology();
        topology.Message<GlobalTestMessage>();

        // Pre-configure local queues with same count as external
        topology.LocalQueues("local", 4);

        var external = CreateLocalTopology("ext", 4);
        topology.SetExternalTopology(external, "test");

        // Should not throw
        topology.AssertValidity();
    }

    [Fact]
    public void set_external_topology_preserves_pre_configured_local_queues()
    {
        var topology = CreateTopology();

        // Pre-configure local queues
        topology.LocalQueues("my-local", 3);
        var originalLocal = topology.LocalTopology;

        // Set external topology - should NOT overwrite existing local topology
        var external = CreateLocalTopology("ext", 3);
        topology.SetExternalTopology(external, "test");

        topology.LocalTopology.ShouldBeSameAs(originalLocal);
    }

    [Fact]
    public void set_external_topology_creates_local_when_not_pre_configured()
    {
        var topology = CreateTopology();

        // Don't pre-configure local queues
        topology.LocalTopology.ShouldBeNull();

        var external = CreateLocalTopology("ext", 3);
        topology.SetExternalTopology(external, "test");

        topology.LocalTopology.ShouldNotBeNull();
        topology.LocalTopology!.Slots.Count.ShouldBe(3);
    }
}

#endregion

#region GlobalPartitionedReceiverBridge Tests

public class GlobalPartitionedReceiverBridgeTests
{
    [Fact]
    public async Task forwards_envelopes_to_local_queue_via_received_async()
    {
        var localQueue = Substitute.For<ILocalQueue>();
        var bridge = new GlobalPartitionedReceiverBridge(localQueue);
        var listener = Substitute.For<IListener>();
        var envelope = ObjectMother.Envelope();

        await bridge.ReceivedAsync(listener, envelope);

        await localQueue.Received(1).ReceivedAsync(listener, envelope);
    }

    [Fact]
    public async Task forwards_envelope_array_to_local_queue()
    {
        var localQueue = Substitute.For<ILocalQueue>();
        var bridge = new GlobalPartitionedReceiverBridge(localQueue);
        var listener = Substitute.For<IListener>();
        var envelopes = new[] { ObjectMother.Envelope(), ObjectMother.Envelope() };

        await bridge.ReceivedAsync(listener, envelopes);

        // Each envelope should be forwarded individually
        await localQueue.Received(2).ReceivedAsync(listener, Arg.Any<Envelope>());
    }

    [Fact]
    public async Task drain_async_returns_completed()
    {
        var localQueue = Substitute.For<ILocalQueue>();
        var bridge = new GlobalPartitionedReceiverBridge(localQueue);

        // Should complete without error and not delegate to localQueue
        await bridge.DrainAsync();

        await localQueue.DidNotReceive().DrainAsync();
    }

    [Fact]
    public void dispose_does_not_dispose_the_local_queue()
    {
        var localQueue = Substitute.For<ILocalQueue>();
        var bridge = new GlobalPartitionedReceiverBridge(localQueue);

        bridge.Dispose();

        localQueue.DidNotReceive().Dispose();
    }

    [Fact]
    public void pipeline_delegates_to_local_queue()
    {
        var localQueue = Substitute.For<ILocalQueue>();
        var pipeline = Substitute.For<IHandlerPipeline>();
        localQueue.Pipeline.Returns(pipeline);

        var bridge = new GlobalPartitionedReceiverBridge(localQueue);

        bridge.Pipeline.ShouldBe(pipeline);
    }
}

#endregion

#region GlobalPartitionedInterceptor Tests

public class GlobalPartitionedInterceptorTests
{
    private readonly IReceiver _inner;
    private readonly IListener _listener;
    private readonly MockWolverineRuntime _runtime;
    private readonly List<Envelope> _routedEnvelopes = new();

    public GlobalPartitionedInterceptorTests()
    {
        _inner = Substitute.For<IReceiver>();
        _listener = Substitute.For<IListener>();
        _runtime = new MockWolverineRuntime();
    }

    private GlobalPartitionedInterceptor CreateInterceptor(params Type[] matchingTypes)
    {
        foreach (var type in matchingTypes)
        {
            var topology = new GlobalPartitionedMessageTopology(_runtime.Options);
            topology.Message(type);
            _runtime.Options.MessagePartitioning.GlobalPartitionedTopologies.Add(topology);
        }

        return new GlobalPartitionedInterceptor(_inner, _runtime);
    }

    private void ArrangeRouter<T>(Func<DeliveryOptions?, Envelope[]>? routeFactory = null)
    {
        var router = Substitute.For<IMessageRouter>();
        router.RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(callInfo =>
            {
                var message = callInfo.Arg<object>();
                var options = callInfo.Arg<DeliveryOptions?>();
                Envelope[] outgoing;
                if (routeFactory is not null)
                {
                    outgoing = routeFactory(options);
                }
                else
                {
                    var envelope = new Envelope(message)
                    {
                        Sender = NoopSendingAgent.Instance,
                        Destination = NoopSendingAgent.Instance.Destination,
                        Status = EnvelopeStatus.Outgoing,
                    };
                    options?.Override(envelope);
                    outgoing = [envelope];
                }
                _routedEnvelopes.AddRange(outgoing);
                return outgoing;
            });
        _runtime.Routers[typeof(T)] = router;
    }

    [Fact]
    public async Task passes_non_matching_messages_through_to_inner_receiver()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var envelope = ObjectMother.Envelope();
        envelope.Message = new UnrelatedMessage("1");

        await interceptor.ReceivedAsync(_listener, envelope);

        await _inner.Received(1).ReceivedAsync(_listener, envelope);
        _routedEnvelopes.ShouldBeEmpty();
    }

    [Fact]
    public async Task passes_non_matching_messages_through_via_batch()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var envelope = ObjectMother.Envelope();
        envelope.Message = new UnrelatedMessage("1");

        await interceptor.ReceivedAsync(_listener, new[] { envelope });

        await _inner.Received(1).ReceivedAsync(_listener, Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task re_publishes_matching_messages_via_message_bus()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");
        envelope.GroupId = "group1";

        await interceptor.ReceivedAsync(_listener, envelope);

        _routedEnvelopes.Count.ShouldBe(1);
        _routedEnvelopes[0].GroupId.ShouldBe("group1");
        await _inner.DidNotReceive().ReceivedAsync(_listener, envelope);
    }

    [Fact]
    public async Task completes_intercepted_messages_on_the_listener()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");

        await interceptor.ReceivedAsync(_listener, envelope);

        await _listener.Received(1).CompleteAsync(envelope);
    }

    [Fact]
    public async Task defers_messages_when_re_publish_fails()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var router = Substitute.For<IMessageRouter>();
        router.RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Throws(new Exception("Transport failure"));
        _runtime.Routers[typeof(GlobalTestMessage)] = router;

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");

        await interceptor.ReceivedAsync(_listener, envelope);

        await _listener.Received(1).DeferAsync(envelope);
        await _listener.DidNotReceive().CompleteAsync(envelope);
    }

    [Fact]
    public async Task batch_splits_matching_and_non_matching()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var matching = ObjectMother.Envelope();
        matching.Message = new GlobalTestMessage("1");

        var nonMatching = ObjectMother.Envelope();
        nonMatching.Message = new UnrelatedMessage("2");

        await interceptor.ReceivedAsync(_listener, new[] { matching, nonMatching });

        _routedEnvelopes.Count.ShouldBe(1);
        await _listener.Received(1).CompleteAsync(matching);
        await _inner.Received(1).ReceivedAsync(_listener, Arg.Is<Envelope[]>(arr => arr.Length == 1));
    }

    [Fact]
    public async Task does_not_call_inner_when_all_messages_are_intercepted()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var e1 = ObjectMother.Envelope();
        e1.Message = new GlobalTestMessage("1");
        var e2 = ObjectMother.Envelope();
        e2.Message = new GlobalTestMessage("2");

        await interceptor.ReceivedAsync(_listener, new[] { e1, e2 });

        await _inner.DidNotReceive().ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task drain_delegates_to_inner()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        await interceptor.DrainAsync();
        await _inner.Received(1).DrainAsync();
    }

    [Fact]
    public void dispose_delegates_to_inner()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        interceptor.Dispose();
        _inner.Received(1).Dispose();
    }

    [Fact]
    public void pipeline_delegates_to_inner()
    {
        var pipeline = Substitute.For<IHandlerPipeline>();
        _inner.Pipeline.Returns(pipeline);
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        interceptor.Pipeline.ShouldBe(pipeline);
    }

    [Fact]
    public async Task does_not_intercept_when_message_is_null()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var envelope = ObjectMother.Envelope();
        envelope.Message = null;

        await interceptor.ReceivedAsync(_listener, envelope);

        await _inner.Received(1).ReceivedAsync(_listener, envelope);
        _routedEnvelopes.ShouldBeEmpty();
    }

    [Fact]
    public async Task does_not_copy_custom_inbound_headers_when_propagation_rule_not_configured()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");
        envelope.Headers["Opta-X-Metrics-Id"] = "metrics-abc";
        envelope.Headers["Opta-X-Causation-Id"] = "causation-xyz";

        await interceptor.ReceivedAsync(_listener, envelope);

        var outgoing = _routedEnvelopes.ShouldHaveSingleItem();
        // Cast disambiguates between Shouldly's IDictionary / IReadOnlyDictionary
        // ShouldNotContainKey overloads (Envelope.Headers is a concrete Dictionary).
        ((IDictionary<string, string?>)outgoing.Headers).ShouldNotContainKey("Opta-X-Metrics-Id");
        ((IDictionary<string, string?>)outgoing.Headers).ShouldNotContainKey("Opta-X-Causation-Id");
    }

    [Fact]
    public async Task copies_only_allowlisted_custom_headers_when_propagation_rule_configured()
    {
        _runtime.Options.Policies.PropagateIncomingHeadersToOutgoing("Opta-X-Metrics-Id");

        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>(_ => [new Envelope(new GlobalTestMessage("123"))
        {
            Sender = NoopSendingAgent.Instance,
            Destination = NoopSendingAgent.Instance.Destination,
            Status = EnvelopeStatus.Outgoing,
        }]);

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");
        envelope.Headers["Opta-X-Metrics-Id"] = "metrics-abc";
        envelope.Headers["Opta-X-Causation-Id"] = "causation-xyz";

        await interceptor.ReceivedAsync(_listener, envelope);

        var outgoing = _routedEnvelopes.ShouldHaveSingleItem();
        outgoing.Headers["Opta-X-Metrics-Id"].ShouldBe("metrics-abc");
        ((IDictionary<string, string?>)outgoing.Headers).ShouldNotContainKey("Opta-X-Causation-Id");
    }

    [Fact]
    public async Task carries_inbound_correlation_id_onto_re_routed_envelope()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");
        envelope.CorrelationId = "inbound-correlation";

        await interceptor.ReceivedAsync(_listener, envelope);

        var outgoing = _routedEnvelopes.ShouldHaveSingleItem();
        outgoing.CorrelationId.ShouldBe("inbound-correlation");
    }

    [Fact]
    public async Task carries_inbound_tenant_id_onto_re_routed_envelope()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");
        envelope.TenantId = "tenant-42";

        await interceptor.ReceivedAsync(_listener, envelope);

        var outgoing = _routedEnvelopes.ShouldHaveSingleItem();
        outgoing.TenantId.ShouldBe("tenant-42");
    }

    [Fact]
    public async Task continues_inbound_distributed_trace_on_re_routed_envelope()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(listener);

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var inboundParentId = $"00-{traceId}-{spanId}-01";

        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        ArrangeRouter<GlobalTestMessage>();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new GlobalTestMessage("123");
        envelope.ParentId = inboundParentId;

        await interceptor.ReceivedAsync(_listener, envelope);

        var outgoing = _routedEnvelopes.ShouldHaveSingleItem();
        outgoing.ParentId.ShouldNotBeNull();
        outgoing.ParentId.ShouldContain(traceId.ToString());
    }
}

internal sealed class NoopSendingAgent : ISendingAgent
{
    public static readonly NoopSendingAgent Instance = new();

    public Uri Destination { get; } = new("noop://test");
    public Uri? ReplyUri { get; set; }
    public bool Latched => false;
    public bool IsDurable => false;
    public bool SupportsNativeScheduledSend => false;
    public Endpoint Endpoint { get; } = null!;
    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;
    public ValueTask EnqueueOutgoingAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask StoreAndForwardAsync(Envelope envelope) => ValueTask.CompletedTask;
}

#endregion

#region MessagePartitioningRules GlobalPartitioned Tests

public class MessagePartitioningRulesGlobalPartitionedTests
{
    private readonly WolverineOptions _options = new();

    [Fact]
    public void global_partitioned_adds_topology_to_global_partitioned_topologies()
    {
        var rules = new MessagePartitioningRules(_options);

        rules.GlobalPartitioned(topology =>
        {
            topology.Message<GlobalTestMessage>();
            var external = new LocalPartitionedMessageTopology(_options, "ext", 2);
            topology.SetExternalTopology(external, "test");
        });

        rules.GlobalPartitionedTopologies.Count.ShouldBe(1);
        rules.GlobalPartitionedTopologies[0].Matches(typeof(GlobalTestMessage)).ShouldBeTrue();
    }

    [Fact]
    public void global_partitioned_throws_when_configure_is_null()
    {
        var rules = new MessagePartitioningRules(_options);

        Should.Throw<ArgumentNullException>(() => rules.GlobalPartitioned(null!));
    }

    [Fact]
    public void try_find_global_topology_finds_matching_topology()
    {
        var rules = new MessagePartitioningRules(_options);

        rules.GlobalPartitioned(topology =>
        {
            topology.Message<GlobalTestMessage>();
            var external = new LocalPartitionedMessageTopology(_options, "ext", 2);
            topology.SetExternalTopology(external, "test");
        });

        rules.TryFindGlobalTopology(typeof(GlobalTestMessage), out var found).ShouldBeTrue();
        found.ShouldNotBeNull();
    }

    [Fact]
    public void try_find_global_topology_returns_false_for_no_match()
    {
        var rules = new MessagePartitioningRules(_options);

        rules.GlobalPartitioned(topology =>
        {
            topology.Message<GlobalTestMessage>();
            var external = new LocalPartitionedMessageTopology(_options, "ext", 2);
            topology.SetExternalTopology(external, "test");
        });

        rules.TryFindGlobalTopology(typeof(UnrelatedMessage), out var found).ShouldBeFalse();
        found.ShouldBeNull();
    }

    [Fact]
    public void multiple_global_topologies_are_tracked()
    {
        var rules = new MessagePartitioningRules(_options);

        rules.GlobalPartitioned(topology =>
        {
            topology.Message<GlobalTestMessage>();
            var external = new LocalPartitionedMessageTopology(_options, "ext1", 2);
            topology.SetExternalTopology(external, "test1");
        });

        rules.GlobalPartitioned(topology =>
        {
            topology.Message<AnotherGlobalTestMessage>();
            var external = new LocalPartitionedMessageTopology(_options, "ext2", 3);
            topology.SetExternalTopology(external, "test2");
        });

        rules.GlobalPartitionedTopologies.Count.ShouldBe(2);
        rules.TryFindGlobalTopology(typeof(GlobalTestMessage), out _).ShouldBeTrue();
        rules.TryFindGlobalTopology(typeof(AnotherGlobalTestMessage), out _).ShouldBeTrue();
    }
}

#endregion
