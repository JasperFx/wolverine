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
/// GH-5926. A buffered receiver settles a broker delivery as soon as it has queued it. For Kafka that completed the
/// record's offset on arrival, so the committed offset moved past records that were still queued or running, and a
/// node that stopped with work in its buffer lost it. A listener that implements
/// <see cref="ISettlesAfterHandlingListener"/> has each delivery settled once its message reaches a terminal instead,
/// and a delivery that never gets there is never settled.
/// </summary>
public class buffered_receiver_settles_after_handling_5926 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<NativeAckPingHandler>();

                // The drain below waits this long for the handler held at its gate
                opts.Durability.DrainTimeout = 250.Milliseconds();
            })
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
    public async Task a_delivery_is_settled_only_after_its_handler_succeeds()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5926-single");
        var receiver = receiverOf(agent);

        var listener = new SettlesAfterHandlingListener();
        await receiver.ReceivedAsync(listener, envelopeFor("single"));

        await NativeAckPingHandler.Entered!.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);

        // Queued and running, but not finished: the broker must still have it
        listener.Completed.ShouldBeEmpty();

        NativeAckPingHandler.Gate!.SetResult();

        var handledFirst = await listener.FirstCompletion.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        handledFirst.ShouldBeTrue("The delivery was settled before its handler had finished");
        listener.Completed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_delivery_received_in_a_batch_is_settled_only_after_its_handler_succeeds()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5926-batch");
        var receiver = receiverOf(agent);

        var listener = new SettlesAfterHandlingListener();
        await receiver.ReceivedAsync(listener, [envelopeFor("batched")]);

        await NativeAckPingHandler.Entered!.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        listener.Completed.ShouldBeEmpty();

        NativeAckPingHandler.Gate!.SetResult();

        var handledFirst = await listener.FirstCompletion.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        handledFirst.ShouldBeTrue("The delivery was settled before its handler had finished");
        listener.Completed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task deliveries_that_never_finish_before_a_drain_are_never_settled()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5926-drain", maxDegreeOfParallelism: 1);
        var receiver = receiverOf(agent);

        var listener = new SettlesAfterHandlingListener();
        await receiver.ReceivedAsync(listener, envelopeFor("running"));
        await NativeAckPingHandler.Entered!.Task.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);

        // Queued behind the handler held at its gate
        var queued = envelopeFor("queued");
        await receiver.ReceivedAsync(listener, queued);

        // What a node shutting down does: latch, then drain past the drain timeout with "running" still held
        ((ILatchedReceiver)receiver).Latch();
        await receiver.DrainAsync();

        // Neither message finished, so neither delivery may be settled
        listener.Completed.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_plain_broker_delivery_is_still_settled_on_arrival()
    {
        await using var agent = await startBufferedAgentAsync("buffered-5926-plain");
        var receiver = receiverOf(agent);

        var listener = new RecordingListener();
        await receiver.ReceivedAsync(listener, envelopeFor("plain"));

        // Unchanged for every other listener: settled while the handler is still held at its gate
        await listener.WaitForSettlement();
        listener.Completed.Count.ShouldBe(1);
        NativeAckPingHandler.Handled.ShouldNotContain("plain");
    }

    private async Task<ListeningAgent> startBufferedAgentAsync(string name, int? maxDegreeOfParallelism = null)
    {
        var endpoint = new StubEndpoint(name, new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.BufferedInMemory;
        if (maxDegreeOfParallelism.HasValue)
        {
            endpoint.MaxDegreeOfParallelism = maxDegreeOfParallelism.Value;
        }

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

    private class SettlesAfterHandlingListener : ISettlesAfterHandlingListener
    {
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<Envelope> Completed { get; } = new();
        public ConcurrentBag<Envelope> Deferred { get; } = new();

        /// <summary>Whether the first completed delivery's handler had finished when it was settled</summary>
        public Task<bool> FirstCompletion => _first.Task;

        public Uri Address { get; } = new("stub://settles-after-handling-5926");
        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            Completed.Add(envelope);
            _first.TrySetResult(envelope.Message is NativeAckPing ping && NativeAckPingHandler.Handled.Contains(ping.Name));
            return ValueTask.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope)
        {
            Deferred.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }
}
