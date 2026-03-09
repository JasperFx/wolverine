using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
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
    private readonly IMessageBus _messageBus;
    private readonly IListener _listener;
    private readonly ILogger _logger;
    private readonly WolverineOptions _options;

    public GlobalPartitionedInterceptorTests()
    {
        _inner = Substitute.For<IReceiver>();
        _messageBus = Substitute.For<IMessageBus>();
        _listener = Substitute.For<IListener>();
        _logger = Substitute.For<ILogger>();
        _options = new WolverineOptions();
    }

    private GlobalPartitionedInterceptor CreateInterceptor(params Type[] matchingTypes)
    {
        var topologies = new List<GlobalPartitionedMessageTopology>();
        foreach (var type in matchingTypes)
        {
            var topology = new GlobalPartitionedMessageTopology(_options);
            topology.Message(type);
            topologies.Add(topology);
        }

        return new GlobalPartitionedInterceptor(_inner, _messageBus, topologies, _logger);
    }

    [Fact]
    public async Task passes_non_matching_messages_through_to_inner_receiver()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var envelope = ObjectMother.Envelope();
        envelope.Message = new UnrelatedMessage("1");

        await interceptor.ReceivedAsync(_listener, envelope);

        await _inner.Received(1).ReceivedAsync(_listener, envelope);
        await _messageBus.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions>());
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
        var message = new GlobalTestMessage("123");
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;
        envelope.GroupId = "group1";

        await interceptor.ReceivedAsync(_listener, envelope);

        await _messageBus.Received(1).PublishAsync(message, Arg.Is<DeliveryOptions>(o =>
            o.GroupId == "group1"));
        await _inner.DidNotReceive().ReceivedAsync(_listener, envelope);
    }

    [Fact]
    public async Task completes_intercepted_messages_on_the_listener()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var message = new GlobalTestMessage("123");
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;

        await interceptor.ReceivedAsync(_listener, envelope);

        await _listener.Received(1).CompleteAsync(envelope);
    }

    [Fact]
    public async Task defers_messages_when_re_publish_fails()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var message = new GlobalTestMessage("123");
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;

        _messageBus.PublishAsync(Arg.Any<GlobalTestMessage>(), Arg.Any<DeliveryOptions>())
            .Returns(ValueTask.FromException(new Exception("Transport failure")));

        await interceptor.ReceivedAsync(_listener, envelope);

        await _listener.Received(1).DeferAsync(envelope);
        await _listener.DidNotReceive().CompleteAsync(envelope);
    }

    [Fact]
    public async Task defers_messages_when_re_publish_fails_single_envelope()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));
        var message = new GlobalTestMessage("123");
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;

        _messageBus.PublishAsync(Arg.Any<GlobalTestMessage>(), Arg.Any<DeliveryOptions>())
            .Returns(ValueTask.FromException(new Exception("Transport failure")));

        await interceptor.ReceivedAsync(_listener, envelope);

        await _listener.Received(1).DeferAsync(envelope);
        await _listener.DidNotReceive().CompleteAsync(envelope);
    }

    [Fact]
    public async Task batch_splits_matching_and_non_matching()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));

        var matching = ObjectMother.Envelope();
        matching.Message = new GlobalTestMessage("1");

        var nonMatching = ObjectMother.Envelope();
        nonMatching.Message = new UnrelatedMessage("2");

        await interceptor.ReceivedAsync(_listener, new[] { matching, nonMatching });

        // Matching message should be re-published
        await _messageBus.Received(1).PublishAsync(Arg.Any<GlobalTestMessage>(), Arg.Any<DeliveryOptions>());
        await _listener.Received(1).CompleteAsync(matching);

        // Non-matching should pass through to inner
        await _inner.Received(1).ReceivedAsync(_listener, Arg.Is<Envelope[]>(arr => arr.Length == 1));
    }

    [Fact]
    public async Task does_not_call_inner_when_all_messages_are_intercepted()
    {
        var interceptor = CreateInterceptor(typeof(GlobalTestMessage));

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
        await _messageBus.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions>());
    }
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
