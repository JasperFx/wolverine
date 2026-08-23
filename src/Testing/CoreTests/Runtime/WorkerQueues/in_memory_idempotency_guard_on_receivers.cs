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
/// GH-3710. The opt-in in-memory guard as the receivers see it. The behaviour deliberately mirrors
/// <c>when_durable_receiver_detects_duplicate_incoming_envelope</c>: a duplicate is settled with the broker
/// so it stops coming back, and it never reaches the handler.
/// </summary>
public class in_memory_idempotency_guard_on_receivers : IAsyncLifetime
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
    }

    public async ValueTask DisposeAsync()
    {
        NativeAckPingHandler.Gate = null;
        await _host.StopAsync();
        _host.Dispose();
    }

    private Endpoint nativeAckEndpoint(bool withGuard)
    {
        var endpoint = new NativeAckStubEndpoint("native-ack-idempotency", new StubTransport());
        if (withGuard)
        {
            endpoint.InMemoryIdempotency = new InMemoryIdempotencySettings();
        }

        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        return endpoint;
    }

    private NativeAckReceiver nativeAckReceiver(Endpoint endpoint)
    {
        return new NativeAckReceiver(endpoint, theRuntime,
            new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));
    }

    private BufferedReceiver bufferedReceiver(bool withGuard)
    {
        var endpoint = new StubEndpoint("buffered-idempotency", new StubTransport());
        if (withGuard)
        {
            endpoint.InMemoryIdempotency = new InMemoryIdempotencySettings();
        }

        endpoint.Mode = EndpointMode.BufferedInMemory;
        endpoint.Compile(theRuntime);

        return new BufferedReceiver(endpoint, theRuntime,
            new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));
    }

    private static Envelope pingEnvelope(string name, Guid? id = null)
    {
        var envelope = new Envelope(new NativeAckPing(name))
            { MessageType = typeof(NativeAckPing).ToMessageTypeName() };

        if (id.HasValue) envelope.Id = id.Value;

        return envelope;
    }

    [Fact]
    public void the_guard_is_off_unless_you_ask_for_it()
    {
        nativeAckEndpoint(false).IdempotencyGuard.ShouldBeNull();
        nativeAckEndpoint(true).IdempotencyGuard.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_redelivery_is_acked_and_never_executed_in_native_ack_mode()
    {
        var receiver = nativeAckReceiver(nativeAckEndpoint(true));
        var listener = new RecordingListener();

        var first = pingEnvelope("one");
        await receiver.ReceivedAsync(listener, first);
        await listener.WaitUntilAtLeast(1);

        // Same message id, brand new delivery -- exactly what a rolling deploy produces when the drain
        // timeout expires with deliveries still unsettled.
        await receiver.ReceivedAsync(listener, pingEnvelope("one", first.Id));
        await listener.WaitUntilAtLeast(2);

        NativeAckPingHandler.Handled.Count.ShouldBe(1);

        // Both deliveries settled: the duplicate is acked-and-dropped rather than left to be redelivered
        // forever, which is what DurableReceiver does when the inbox INSERT hits the primary key.
        listener.Completed.Count.ShouldBe(2);
        listener.Deferred.Count.ShouldBe(0);
    }

    [Fact]
    public async Task without_the_guard_a_redelivery_runs_again()
    {
        var receiver = nativeAckReceiver(nativeAckEndpoint(false));
        var listener = new RecordingListener();

        var first = pingEnvelope("one");
        await receiver.ReceivedAsync(listener, first);
        await listener.WaitUntilAtLeast(1);

        await receiver.ReceivedAsync(listener, pingEnvelope("one", first.Id));
        await listener.WaitUntilAtLeast(2);

        NativeAckPingHandler.Handled.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_concurrent_duplicate_is_dropped_rather_than_queued_up_to_run_again()
    {
        var receiver = nativeAckReceiver(nativeAckEndpoint(true));
        var listener = new RecordingListener();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Gate = gate;

        var first = pingEnvelope("one");
        await receiver.ReceivedAsync(listener, first);

        // The original is parked inside the handler. The duplicate has to be turned away on the in-flight
        // set, not merely on the processed set.
        await receiver.ReceivedAsync(listener, pingEnvelope("one", first.Id));
        await listener.WaitUntilAtLeast(1);

        listener.Completed.Count.ShouldBe(1);
        NativeAckPingHandler.Handled.ShouldBeEmpty();

        gate.SetResult();
        await listener.WaitUntilAtLeast(2);

        NativeAckPingHandler.Handled.Count.ShouldBe(1);
    }

    /// <summary>
    /// The failure path, and the reason the guard has a Release() at all: a delivery handed back to the
    /// broker WILL come back, and suppressing that redelivery would convert a retry into a lost message.
    /// This also covers the guard living on the endpoint rather than the receiver -- the second receiver is
    /// a rebuild of the first, which is what a listener restart or back-pressure recovery does.
    /// </summary>
    [Fact]
    public async Task a_deferred_delivery_is_not_remembered_and_still_runs_when_it_comes_back()
    {
        var endpoint = nativeAckEndpoint(true);

        var latched = nativeAckReceiver(endpoint);
        var listener = new RecordingListener();
        latched.Latch();

        var envelope = pingEnvelope("one");
        await latched.ReceivedAsync(listener, envelope);
        await latched.DrainAsync();

        listener.Deferred.Count.ShouldBe(1);
        NativeAckPingHandler.Handled.ShouldBeEmpty();

        var rebuilt = nativeAckReceiver(endpoint);
        await rebuilt.ReceivedAsync(listener, pingEnvelope("one", envelope.Id));
        await listener.WaitUntilAtLeast(2);

        NativeAckPingHandler.Handled.Count.ShouldBe(1);
        listener.Completed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_redelivery_is_acked_and_never_executed_in_buffered_mode()
    {
        var receiver = bufferedReceiver(true);
        var listener = new RecordingListener();

        var first = pingEnvelope("one");
        await ((IReceiver)receiver).ReceivedAsync(listener, first);

        // Buffered acks at receipt, so settlement says nothing about the handler. Wait on the handler.
        await waitForHandledCount(1);

        // The duplicate is dropped synchronously inside ReceivedAsync, so there is no race to wait out here.
        await ((IReceiver)receiver).ReceivedAsync(listener, pingEnvelope("one", first.Id));

        NativeAckPingHandler.Handled.Count.ShouldBe(1);
        listener.Completed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task without_the_guard_buffered_mode_runs_a_redelivery_again()
    {
        var receiver = bufferedReceiver(false);
        var listener = new RecordingListener();

        var first = pingEnvelope("one");
        await ((IReceiver)receiver).ReceivedAsync(listener, first);
        await listener.WaitUntilAtLeast(1);

        await ((IReceiver)receiver).ReceivedAsync(listener, pingEnvelope("one", first.Id));
        await listener.WaitUntilAtLeast(2);

        await waitForHandledCount(2);
        NativeAckPingHandler.Handled.Count.ShouldBe(2);
    }

    private static async Task waitForHandledCount(int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (NativeAckPingHandler.Handled.Count < count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Yield();
        }
    }
}
