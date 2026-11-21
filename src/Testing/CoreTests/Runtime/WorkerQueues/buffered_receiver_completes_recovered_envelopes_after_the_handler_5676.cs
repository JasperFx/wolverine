using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5676. A buffered receiver settles a broker delivery as soon as it arrives. It did the same to envelopes the
/// durability agent recovered from the message store (a dead letter replay, a scheduled message firing), so their
/// inbox rows were marked Handled from the receiver's completion block, on its own schedule. A tracked session could
/// finish before the row was marked (RabbitMQ Bug_1594, BufferedInMemory), and a node that stopped before the
/// handler ran had already marked the row and lost the message. A recovered envelope is now completed by the
/// handler pipeline, after the handler succeeds and before the tracked session hears that it did.
/// </summary>
public class buffered_receiver_completes_recovered_envelopes_after_the_handler_5676 : IAsyncLifetime
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
        NativeAckPingHandler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        NativeAckPingHandler.Gate?.TrySetResult();
        NativeAckPingHandler.Gate = null;
        NativeAckPingHandler.Entered = null;
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_recovered_envelope_is_completed_only_after_its_handler_succeeds()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5676-recovered");
        var receiver = receiverOf(agent);

        var recovery = new RecordingRecoveryListener("recovered");
        await receiver.ReceivedAsync(recovery, [envelopeFor("recovered")]);

        await NativeAckPingHandler.Entered!.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);

        // The handler is still held at its gate, so nothing may have touched the inbox row yet
        recovery.Completed.ShouldBeEmpty();

        NativeAckPingHandler.Gate!.SetResult();

        var handledFirst = await recovery.FirstCompletion.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        handledFirst.ShouldBeTrue("The inbox row was marked Handled before the handler had finished");
        recovery.Completed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_broker_delivery_is_still_settled_on_arrival()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5676-delivery");
        var receiver = receiverOf(agent);

        var listener = new RecordingListener();
        await receiver.ReceivedAsync(listener, [envelopeFor("delivery")]);

        // Buffered semantics are unchanged for a delivery from the broker: it is settled while the handler is
        // still held at its gate
        await listener.WaitForSettlement();
        listener.Completed.Count.ShouldBe(1);
        NativeAckPingHandler.Handled.ShouldNotContain("delivery");
    }

    [Fact]
    public async Task the_listening_agent_replays_a_stored_envelope_through_a_recovery_listener()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5676-replay");

        var envelope = envelopeFor("replayed");
        NativeAckPingHandler.Gate!.SetResult();

        await agent.EnqueueDirectlyAsync([envelope]);

        // The callback the agent hands over is what makes the receiver hold the completion for the pipeline
        envelope.Listener.ShouldBeAssignableTo<IRecoveredEnvelopeListener>();
    }

    private async Task<ListeningAgent> startBufferedAgentAsync(string name)
    {
        var endpoint = new StubEndpoint(name, new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, theRuntime);
        await agent.StartAsync();

        receiverOf(agent).ShouldBeOfType<BufferedReceiver>();

        return agent;
    }

    private static Envelope envelopeFor(string name)
    {
        return new Envelope(new NativeAckPing(name))
        {
            MessageType = typeof(NativeAckPing).ToMessageTypeName()
        };
    }

    private static IReceiver receiverOf(ListeningAgent agent)
    {
        var field = typeof(ListeningAgent).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IReceiver)field.GetValue(agent)!;
    }

    private class RecordingRecoveryListener : IRecoveredEnvelopeListener
    {
        private readonly string _name;
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingRecoveryListener(string name)
        {
            _name = name;
        }

        public ConcurrentBag<Envelope> Completed { get; } = new();

        /// <summary>Whether the handler had finished when the envelope was first completed</summary>
        public Task<bool> FirstCompletion => _first.Task;

        public Uri Address { get; } = new("stub://recovery-5676");
        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            Completed.Add(envelope);
            _first.TrySetResult(NativeAckPingHandler.Handled.Contains(_name));
            return ValueTask.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }
}
