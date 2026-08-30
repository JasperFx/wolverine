using System.Reflection;
using JasperFx.Blocks;
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
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4191. The last two raw-_receiver type tests in ListeningAgent, plus one unguarded wrap in the same method.
///
/// ReceiverHasFaulted and StartAsync's "never re-attach a listener to a terminally faulted receiver" guard both
/// tested IFaultTrackingReceiver against the raw field. Neither pass-through wrapper implements that interface --
/// only BufferedReceiver, DurableReceiver and NativeAckReceiver do -- so for any endpoint carrying an incoming
/// envelope rule (which includes a bare endpoint-level MessageType or TenantId) a terminally faulted receiver
/// reported healthy forever and was never rebuilt. That is exactly the silently-dead-listener case CritterWatch#942
/// added the machinery to catch, defeated on the endpoints most likely to be non-trivially configured.
///
/// Third, unrelated to Unwrap() but in the same method: StartAsync is also the back pressure RESUME path, and its
/// GlobalPartitionedInterceptor wrap was unconditional, so every latch/resume cycle added a layer.
/// </summary>
public class wrapped_receiver_fault_detection_4191 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        theRuntime.Options.MessagePartitioning.GlobalPartitionedTopologies.Clear();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_faulted_receiver_behind_an_incoming_rule_is_reported_as_faulted()
    {
        var endpoint = bufferedEndpoint("fault-4191");

        await using var agent = await startAgentAsync(endpoint);

        // Guard against a vacuous pass: an unwrapped receiver was already reported correctly.
        var wrapper = receiverOf(agent).ShouldBeOfType<ReceiverWithRules>();
        var inner = wrapper.Inner.ShouldBeOfType<BufferedReceiver>();

        agent.ReceiverHasFaulted.ShouldBeFalse();

        faultTerminally(inner);

        // The receiver itself knows. Everything that has to act on it read through the wrapper and saw nothing.
        inner.HasFaulted.ShouldBeTrue();
        agent.ReceiverHasFaulted.ShouldBeTrue();
    }

    /// <summary>
    /// The rebuild half, driven the way it actually happens rather than through a stop/start. StopAndDrainCoreAsync
    /// nulls _receiver before it drains, so RestartAsync never reaches the guard at all -- a stop-then-start test
    /// passes on unfixed code and proves nothing. MarkAsTooBusyAndStopReceivingAsync deliberately KEEPS the
    /// receiver (its queue is what has to drain) and only drops the Listener, which is both the path that reaches
    /// the guard and the literal CritterWatch#942 scenario: a faulted receiver's frozen QueueCount is what latches
    /// the listener in the first place.
    /// </summary>
    [Fact]
    public async Task a_faulted_receiver_behind_an_incoming_rule_is_rebuilt_on_resume()
    {
        var endpoint = bufferedEndpoint("rebuild-4191");

        await using var agent = await startAgentAsync(endpoint);

        var inner = receiverOf(agent).ShouldBeOfType<ReceiverWithRules>().Inner.ShouldBeOfType<BufferedReceiver>();
        faultTerminally(inner);

        await agent.MarkAsTooBusyAndStopReceivingAsync();
        agent.Status.ShouldBe(ListeningStatus.TooBusy);

        // Still the same faulted receiver -- MarkAsTooBusy deliberately does not touch it.
        receiverOf(agent).ShouldBeOfType<ReceiverWithRules>().Inner.ShouldBeSameAs(inner);

        await agent.StartAsync();

        // Rebuilt, and re-wrapped: the endpoint still has its incoming rule.
        var rebuilt = receiverOf(agent).ShouldBeOfType<ReceiverWithRules>();
        rebuilt.Inner.ShouldBeOfType<BufferedReceiver>().ShouldNotBeSameAs(inner);
        agent.ReceiverHasFaulted.ShouldBeFalse();
    }

    /// <summary>
    /// Third defect, same method. StartAsync's `??=` guards the rebuild but the interceptor wrap was unconditional,
    /// and StartAsync is the back pressure resume path, so the chain grew by one layer per latch/resume cycle
    /// forever. Never wrong -- every layer delegates -- just unbounded.
    /// </summary>
    [Fact]
    public async Task resuming_from_back_pressure_does_not_stack_another_interceptor()
    {
        var topology = new GlobalPartitionedMessageTopology(theRuntime.Options);
        topology.Message<UnrelatedPartitionedMessage>();
        theRuntime.Options.MessagePartitioning.GlobalPartitionedTopologies.Add(topology);

        var endpoint = new StubEndpoint("nesting-4191", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;

        await using var agent = await startAgentAsync(endpoint);

        var first = receiverOf(agent).ShouldBeOfType<GlobalPartitionedInterceptor>();
        first.Inner.ShouldBeOfType<BufferedReceiver>();

        for (var i = 0; i < 3; i++)
        {
            await agent.MarkAsTooBusyAndStopReceivingAsync();
            agent.Status.ShouldBe(ListeningStatus.TooBusy);
            await agent.StartAsync();
            agent.Status.ShouldBe(ListeningStatus.Accepting);
        }

        // One layer, still over the same receiver. Pre-fix this was an interceptor over an interceptor over an
        // interceptor over the BufferedReceiver, and nothing would ever have unwound it.
        var after = receiverOf(agent).ShouldBeOfType<GlobalPartitionedInterceptor>();
        after.Inner.ShouldBeOfType<BufferedReceiver>();
    }

    private static StubEndpoint bufferedEndpoint(string name)
    {
        var endpoint = new StubEndpoint(name, new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;

        // The cheapest real incoming rule -- this alone is what installs ReceiverWithRules.
        endpoint.TenantId = "one";

        return endpoint;
    }

    /// <summary>
    /// Flip HasFaulted through the production path rather than by writing the backing field. The receivers assign
    /// their own onBlockError onto IBlock.OnError, and that handler sets HasFaulted on exactly one condition --
    /// a null envelope, which is the jasperfx#506 terminal-fault signature the block itself raises.
    /// </summary>
    private static void faultTerminally(BufferedReceiver receiver)
    {
        var field = typeof(BufferedReceiver)
            .GetField("_receivingBlock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var block = (IBlock<Envelope>)field.GetValue(receiver)!;

        // The delegate is Action<T, Exception>; the receivers' own handler declares the envelope nullable and
        // branches on exactly that, so null! is the terminal-fault signature and not a lie about the contract.
        block.OnError!(null!, new DivideByZeroException("terminal"));
    }

    private async Task<ListeningAgent> startAgentAsync(Endpoint endpoint)
    {
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, theRuntime);
        await agent.StartAsync();

        return agent;
    }

    private static IReceiver receiverOf(ListeningAgent agent)
    {
        var field = typeof(ListeningAgent).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IReceiver)field.GetValue(agent)!;
    }
}
