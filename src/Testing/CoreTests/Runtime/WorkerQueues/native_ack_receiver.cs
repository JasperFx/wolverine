using System.Collections.Concurrent;
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
using JasperFx.Core;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

#region test message + handler

public record NativeAckPing(string Name);

public class NativeAckPingHandler
{
    public static readonly ConcurrentBag<string> Handled = new();

    /// <summary>Gate so a test can observe the receiver mid-flight without Task.Delay. </summary>
    public static TaskCompletionSource? Gate;

    /// <summary>Signals that the handler has actually started, so a test can latch mid-flight rather than
    /// racing the block.</summary>
    public static TaskCompletionSource? Entered;

    public async Task Handle(NativeAckPing message)
    {
        Entered?.TrySetResult();

        if (Gate != null)
        {
            await Gate.Task;
        }

        Handled.Add(message.Name);
    }
}

#endregion

/// <summary>
/// GH-3708. The defining behaviour of EndpointMode.NativeAck: the broker delivery is NOT settled at receipt,
/// only when the handler pipeline reaches a terminal.
/// </summary>
public class native_ack_receiver : IAsyncLifetime
{
    private IHost _host = null!;
    private IWolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<NativeAckPingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = _host.Services.GetRequiredService<IWolverineRuntime>();
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

    private NativeAckReceiver receiverFor(Action<Endpoint>? configure = null)
    {
        var endpoint = new NativeAckStubEndpoint("native-ack", new StubTransport());
        configure?.Invoke(endpoint);
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        return new NativeAckReceiver(endpoint, theRuntime, new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));
    }

    private static Envelope pingEnvelope(string name = "one")
    {
        return new Envelope(new NativeAckPing(name)) { MessageType = typeof(NativeAckPing).ToMessageTypeName() };
    }

    [Fact]
    public async Task settles_exactly_once_and_only_after_the_handler_succeeds()
    {
        var receiver = receiverFor();
        var listener = new RecordingListener();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Gate = gate;

        await receiver.ReceivedAsync(listener, pingEnvelope());

        // Mid-flight: the delivery has been received and enqueued, and is deliberately still unacknowledged.
        // This is the entire difference from BufferedInMemory, which acks right here.
        await listener.WaitUntilAtLeast(0);
        listener.Completed.Count.ShouldBe(0);
        listener.Deferred.Count.ShouldBe(0);

        gate.SetResult();

        await listener.WaitForSettlement();

        listener.Completed.Count.ShouldBe(1);
        listener.Deferred.Count.ShouldBe(0);
        NativeAckPingHandler.Handled.ShouldContain("one");
    }

    [Fact]
    public async Task an_expired_envelope_is_settled_without_ever_reaching_the_handler()
    {
        var receiver = receiverFor();
        var listener = new RecordingListener();

        var envelope = pingEnvelope("expired");
        envelope.DeliverBy = DateTimeOffset.UtcNow.Subtract(1.Minutes());

        await receiver.ReceivedAsync(listener, envelope);
        await receiver.DrainAsync();

        // Acked so the broker stops redelivering something nobody will ever process
        listener.Completed.Count.ShouldBe(1);
        NativeAckPingHandler.Handled.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_latched_receiver_hands_the_delivery_back_to_the_broker()
    {
        var receiver = receiverFor();
        var listener = new RecordingListener();

        receiver.Latch();
        await receiver.ReceivedAsync(listener, pingEnvelope("latched"));
        await receiver.DrainAsync();

        listener.Deferred.Count.ShouldBe(1);
        listener.Completed.Count.ShouldBe(0);
        NativeAckPingHandler.Handled.ShouldBeEmpty();
    }

    /// <summary>
    /// GH-3709. The half of the drain contract that an exclusive listener handoff depends on: once the
    /// receiver has been latched, <c>DrainAsync</c> must not report done while a handler is still running.
    /// If it returns early the listener is disposed underneath live work, the still-unsettled deliveries go
    /// back to the broker, and the node taking the listener over runs them concurrently with the node that
    /// is still finishing them.
    /// </summary>
    [Fact]
    public async Task draining_a_latched_receiver_waits_for_the_in_flight_handler()
    {
        var receiver = receiverFor();
        var listener = new RecordingListener();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Gate = gate;
        NativeAckPingHandler.Entered = entered;

        await receiver.ReceivedAsync(listener, pingEnvelope("in-flight"));
        await entered.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        receiver.Latch();
        var drain = receiver.DrainAsync().AsTask();

        var raced = await Task.WhenAny(drain, Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken));
        raced.ShouldNotBeSameAs(drain);

        gate.SetResult();

        await drain.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        listener.Completed.Count.ShouldBe(1);
        listener.Deferred.Count.ShouldBe(0);
        NativeAckPingHandler.Handled.ShouldContain("in-flight");
    }

    /// <summary>
    /// The reason GH-4013 added the per-envelope channel-source overload. With ListenerCount > 1 the receiver
    /// is shared across listeners, so a single bound IChannelCallback would settle the wrong delivery.
    /// </summary>
    [Fact]
    public async Task each_delivery_settles_against_its_own_listener()
    {
        var receiver = receiverFor();
        var listenerA = new RecordingListener();
        var listenerB = new RecordingListener();

        await receiver.ReceivedAsync(listenerA, pingEnvelope("a"));
        await receiver.ReceivedAsync(listenerB, pingEnvelope("b"));

        // NOT DrainAsync() here: an unlatched drain deliberately does not wait on the block (the same
        // re-entrancy guard BufferedReceiver has, so a pipeline-triggered pause cannot deadlock itself).
        await listenerA.WaitForSettlement();
        await listenerB.WaitForSettlement();

        listenerA.Completed.Count.ShouldBe(1);
        listenerB.Completed.Count.ShouldBe(1);
        listenerA.Completed.Single().Message.ShouldBeOfType<NativeAckPing>().Name.ShouldBe("a");
        listenerB.Completed.Single().Message.ShouldBeOfType<NativeAckPing>().Name.ShouldBe("b");
    }

    [Fact]
    public void back_pressure_is_the_brokers_prefetch_window_not_an_agent()
    {
        var endpoint = new NativeAckStubEndpoint("native-ack", new StubTransport());
        endpoint.Mode = EndpointMode.NativeAck;

        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();
    }
}

/// <summary>Stands in for RabbitMQ until it opts in.</summary>
internal class NativeAckStubEndpoint : StubEndpoint
{
    public NativeAckStubEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
    {
    }

    protected override bool supportsNativeAck => true;
}

/// <summary>Records how each delivery was settled, and by which listener.</summary>
internal class RecordingListener : IListener
{
    public ConcurrentBag<Envelope> Completed { get; } = new();
    public ConcurrentBag<Envelope> Deferred { get; } = new();

    public Uri Address { get; } = new("stub://native-ack");
    public IHandlerPipeline? Pipeline => null;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        Completed.Add(envelope);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        Deferred.Add(envelope);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask StopAsync() => ValueTask.CompletedTask;

    public async Task WaitUntilAtLeast(int settled)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Completed.Count + Deferred.Count < settled && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Yield();
        }
    }

    public Task WaitForSettlement() => WaitUntilAtLeast(1);
}
