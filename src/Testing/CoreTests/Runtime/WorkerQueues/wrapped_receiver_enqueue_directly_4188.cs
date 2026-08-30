using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4188. GH-4011 gave EnqueueDirectlyAsync a branch for NativeAckReceiver, but the type switch reads the raw
/// _receiver field -- and that field is routinely a pass-through wrapper instead of the receiver being tested for.
///
/// ReceiverWithRules (any incoming envelope rule: an IncomingRules entry, an endpoint MessageType, an endpoint
/// TenantId) unconditionally implements ILocalQueue, so a wrapped NativeAckReceiver or InlineReceiver matches the
/// ILocalQueue branch ahead of its own and then throws from inside ReceiverWithRules.EnqueueAsync, whose Inner is
/// not a local queue. Same exception, same call sites -- DLQ replay per GH-1942 and scheduled-message firing --
/// as the bug GH-4011 was supposed to have closed.
///
/// GlobalPartitionedInterceptor is the second wrapper on the same path; it is not an ILocalQueue at all, so
/// everything behind it fell through to the throwing else.
/// </summary>
public class wrapped_receiver_enqueue_directly_4188 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
        NativeAckPingHandler.Handled.Clear();
        NativeAckPingHandler.Gate = null;
        NativeAckPingHandler.Entered = null;
    }

    public async ValueTask DisposeAsync()
    {
        NativeAckPingHandler.Gate = null;
        NativeAckPingHandler.Entered = null;
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task native_ack_endpoint_with_an_incoming_rule_still_reaches_its_receiver()
    {
        var endpoint = new NativeAckStubEndpoint("na-4188", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;

        // The cheapest real incoming rule. Anything RulesForIncoming() yields -- an IncomingRules entry or an
        // endpoint-level MessageType -- produces the same wrapper.
        endpoint.TenantId = "one";

        await using var agent = await startAgentAsync(endpoint);

        // Guard against a vacuous pass: without the wrapper this is the plain GH-4011 test.
        receiverOf(agent).ShouldBeOfType<ReceiverWithRules>()
            .Inner.ShouldBeOfType<NativeAckReceiver>();

        var envelope = envelopeFor("na-rule");

        await agent.EnqueueDirectlyAsync([envelope]);

        await waitForHandledAsync("na-rule");

        // The wrapper still has to do its job -- dispatching around it would silently drop the rules.
        envelope.TenantId.ShouldBe("one");
    }

    [Fact]
    public async Task inline_endpoint_with_an_incoming_rule_still_reaches_its_receiver()
    {
        var endpoint = new StubEndpoint("inline-4188", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.Inline;
        endpoint.TenantId = "two";

        await using var agent = await startAgentAsync(endpoint);

        receiverOf(agent).ShouldBeOfType<ReceiverWithRules>()
            .Inner.ShouldBeOfType<InlineReceiver>();

        var envelope = envelopeFor("inline-rule");

        await agent.EnqueueDirectlyAsync([envelope]);

        await waitForHandledAsync("inline-rule");
        envelope.TenantId.ShouldBe("two");
    }

    /// <summary>
    /// The quieter half of the same defect: a wrapped BufferedReceiver matches the generic ILocalQueue branch
    /// instead of its own, so the replay is enqueued rather than dispatched through a
    /// RetryOnInlineChannelCallback -- and that callback is the only thing that marks the inbox row handled on
    /// completion (GH-1942). The message runs; the row is left behind.
    /// </summary>
    [Fact]
    public async Task buffered_endpoint_with_an_incoming_rule_still_takes_the_buffered_branch()
    {
        var endpoint = new StubEndpoint("buffered-4188", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.TenantId = "three";

        await using var agent = await startAgentAsync(endpoint);

        receiverOf(agent).ShouldBeOfType<ReceiverWithRules>()
            .Inner.ShouldBeOfType<BufferedReceiver>();

        var envelope = envelopeFor("buffered-rule");

        await agent.EnqueueDirectlyAsync([envelope]);

        await waitForHandledAsync("buffered-rule");

        // BufferedReceiver.ReceivedAsync stamps the listener it was handed; the ILocalQueue branch never
        // supplies one. This is the observable difference between the two branches.
        envelope.Listener.ShouldBeOfType<RetryOnInlineChannelCallback>();
        envelope.TenantId.ShouldBe("three");
    }

    [Fact]
    public async Task native_ack_endpoint_behind_a_global_partitioned_interceptor_still_reaches_its_receiver()
    {
        // A topology over some other message type: the interceptor gets installed, but NativeAckPing itself
        // passes straight through to the inner receiver rather than being re-routed.
        var topology = new GlobalPartitionedMessageTopology(theRuntime.Options);
        topology.Message<UnrelatedPartitionedMessage>();
        theRuntime.Options.MessagePartitioning.GlobalPartitionedTopologies.Add(topology);

        try
        {
            var endpoint = new NativeAckStubEndpoint("na-4188-gp", new StubTransport()) { IsListener = true };
            endpoint.Mode = EndpointMode.NativeAck;

            await using var agent = await startAgentAsync(endpoint);

            receiverOf(agent).ShouldBeOfType<GlobalPartitionedInterceptor>();

            await agent.EnqueueDirectlyAsync([envelopeFor("na-interceptor")]);

            await waitForHandledAsync("na-interceptor");
        }
        finally
        {
            theRuntime.Options.MessagePartitioning.GlobalPartitionedTopologies.Clear();
        }
    }

    /// <summary>
    /// The same blindness on the other consumer of the receiver's real type. LatchReceiver hand-rolled a
    /// one-level ReceiverWithRules unwrap (GH-3709), so anything behind a GlobalPartitionedInterceptor was
    /// never latched -- and an unlatched receiver's DrainAsync returns immediately instead of waiting for
    /// in-flight handlers.
    /// </summary>
    [Fact]
    public async Task latch_receiver_reaches_through_a_global_partitioned_interceptor()
    {
        var topology = new GlobalPartitionedMessageTopology(theRuntime.Options);
        topology.Message<UnrelatedPartitionedMessage>();
        theRuntime.Options.MessagePartitioning.GlobalPartitionedTopologies.Add(topology);

        try
        {
            var endpoint = new NativeAckStubEndpoint("na-4188-latch", new StubTransport()) { IsListener = true };
            endpoint.Mode = EndpointMode.NativeAck;

            await using var agent = await startAgentAsync(endpoint);

            var receiver = receiverOf(agent).ShouldBeOfType<GlobalPartitionedInterceptor>();

            agent.LatchReceiver();

            var listener = new RecordingListener();
            await receiver.ReceivedAsync(listener, envelopeFor("latched"));

            // A latched NativeAckReceiver hands the delivery straight back to the broker rather than executing it.
            await listener.WaitUntilAtLeast(1);
            listener.Deferred.Count.ShouldBe(1);
            listener.Completed.ShouldBeEmpty();
            NativeAckPingHandler.Handled.ShouldNotContain("latched");
        }
        finally
        {
            theRuntime.Options.MessagePartitioning.GlobalPartitionedTopologies.Clear();
        }
    }

    private async Task<ListeningAgent> startAgentAsync(Endpoint endpoint)
    {
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, theRuntime);
        await agent.StartAsync();

        return agent;
    }

    private static Envelope envelopeFor(string name)
    {
        return new Envelope(new NativeAckPing(name))
        {
            MessageType = typeof(NativeAckPing).ToMessageTypeName()
        };
    }

    private static async Task waitForHandledAsync(string name)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!NativeAckPingHandler.Handled.Contains(name) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Yield();
        }

        NativeAckPingHandler.Handled.ShouldContain(name);
    }

    private static IReceiver receiverOf(ListeningAgent agent)
    {
        var field = typeof(ListeningAgent).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IReceiver)field.GetValue(agent)!;
    }
}

public record UnrelatedPartitionedMessage(string Name);
