using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5533. The in-memory idempotency guard on a native-ack listener has to tell a redelivery of a message that
/// is STILL IN FLIGHT from one that was already processed. Acknowledging the former settles the message at the
/// broker underneath the original delivery, so a node that drains or dies mid-flight loses it -- which is what
/// TestAllTransports caught on Pulsar. An in-flight duplicate is held unsettled and settled with the original's
/// outcome; only an already-processed duplicate is acknowledged.
/// </summary>
public class native_ack_inflight_duplicates_5533 : IAsyncLifetime
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
        NativeAckPingHandler.Gate?.TrySetResult();
        NativeAckPingHandler.Gate = null;
        NativeAckPingHandler.Entered = null;
        await _host.StopAsync();
        _host.Dispose();
    }

    // ---- the guard ----

    private static GenerationalIdempotencyGuard guard(TimeSpan? window = null, Func<DateTimeOffset>? now = null)
    {
        var settings = new InMemoryIdempotencySettings();
        if (window.HasValue) settings.Window = window.Value;
        return new GenerationalIdempotencyGuard(settings, MessageIdentity.IdOnly, now ?? (() => DateTimeOffset.UtcNow));
    }

    private static Envelope envelope(Guid? id = null)
    {
        return new Envelope { Id = id ?? Guid.NewGuid(), Destination = new Uri("stub://one") };
    }

    [Fact]
    public void the_guard_tells_in_flight_from_already_processed()
    {
        var theGuard = guard();
        var original = envelope();

        theGuard.Claim(original, false).ShouldBe(IdempotencyClaim.Claimed);
        theGuard.Claim(envelope(original.Id), false).ShouldBe(IdempotencyClaim.InFlight);

        theGuard.MarkProcessed(original);

        theGuard.Claim(envelope(original.Id), false).ShouldBe(IdempotencyClaim.AlreadyProcessed);
    }

    [Fact]
    public void a_held_duplicate_is_handed_back_when_the_original_is_processed()
    {
        var theGuard = guard();
        var original = envelope();
        var duplicate = envelope(original.Id);

        theGuard.Claim(original, false).ShouldBe(IdempotencyClaim.Claimed);
        theGuard.Claim(duplicate, true).ShouldBe(IdempotencyClaim.InFlight);
        theGuard.HeldCount.ShouldBe(1);

        theGuard.MarkProcessed(original).ShouldHaveSingleItem().ShouldBeSameAs(duplicate);
        theGuard.HeldCount.ShouldBe(0);
    }

    [Fact]
    public void a_held_duplicate_is_handed_back_when_the_original_is_released()
    {
        var theGuard = guard();
        var original = envelope();
        var duplicate = envelope(original.Id);

        theGuard.Claim(original, false);
        theGuard.Claim(duplicate, true);

        theGuard.Release(original).ShouldHaveSingleItem().ShouldBeSameAs(duplicate);

        // Released, not remembered: the next delivery is allowed to run
        theGuard.Claim(envelope(original.Id), false).ShouldBe(IdempotencyClaim.Claimed);
    }

    [Fact]
    public void the_two_argument_overload_does_not_hold_anything()
    {
        var theGuard = guard();
        var original = envelope();

        theGuard.TryBeginProcessing(original).ShouldBeTrue();
        theGuard.TryBeginProcessing(envelope(original.Id)).ShouldBeFalse();

        theGuard.HeldCount.ShouldBe(0);
        theGuard.MarkProcessed(original).ShouldBeEmpty();
    }

    [Fact]
    public void an_entry_holding_duplicates_survives_rotation()
    {
        var time = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var theGuard = guard(1.Minutes(), () => time);

        var original = envelope();
        var duplicate = envelope(original.Id);
        theGuard.Claim(original, false);
        theGuard.Claim(duplicate, true);

        // Far past two rotations, which drops every in-flight id that is NOT holding a delivery
        var other = envelope();
        theGuard.Claim(other, false);
        for (var i = 0; i < 5; i++)
        {
            time = time.AddMinutes(1);
            theGuard.Claim(envelope(), false);
        }

        theGuard.HeldCount.ShouldBe(1);
        theGuard.MarkProcessed(original).ShouldHaveSingleItem().ShouldBeSameAs(duplicate);
    }

    // ---- the receiver ----

    private (NativeAckReceiver Receiver, GenerationalIdempotencyGuard Guard) receiverFor(int parallelism = 4)
    {
        var endpoint = new NativeAckStubEndpoint("native-ack-5533", new StubTransport())
        {
            InMemoryIdempotency = new InMemoryIdempotencySettings(),
            MaxDegreeOfParallelism = parallelism
        };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var receiver = new NativeAckReceiver(endpoint, theRuntime,
            new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));

        return (receiver, (GenerationalIdempotencyGuard)endpoint.IdempotencyGuard!);
    }

    private static Envelope ping(string name, Guid? id = null)
    {
        var envelope = new Envelope(new NativeAckPing(name)) { MessageType = typeof(NativeAckPing).ToMessageTypeName() };
        if (id.HasValue) envelope.Id = id.Value;
        return envelope;
    }

    private static async Task waitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }

    private static (TaskCompletionSource Gate, TaskCompletionSource Entered) parkHandler()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeAckPingHandler.Gate = gate;
        NativeAckPingHandler.Entered = entered;
        return (gate, entered);
    }

    [Fact]
    public async Task an_in_flight_redelivery_is_neither_acked_nor_nacked()
    {
        var (receiver, theGuard) = receiverFor();
        var (gate, entered) = parkHandler();

        var originalListener = new RecordingListener();
        var redeliveryListener = new RecordingListener();
        var original = ping("in-flight");

        await receiver.ReceivedAsync(originalListener, original);
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The broker redelivers it while the handler is still parked (Pulsar's RedeliverUnacknowledgedMessages on
        // stop, a closed RabbitMQ channel, an expired SQS visibility timeout...)
        await receiver.ReceivedAsync(redeliveryListener, ping("in-flight", original.Id));

        // Give a wrong settle every chance to land
        await Task.Delay(250, TestContext.Current.CancellationToken);

        redeliveryListener.Completed.ShouldBeEmpty();
        redeliveryListener.Deferred.ShouldBeEmpty();
        originalListener.Completed.ShouldBeEmpty();
        theGuard.HeldCount.ShouldBe(1);

        gate.SetResult();
    }

    [Fact]
    public async Task the_held_redelivery_is_acked_once_the_original_succeeds()
    {
        var (receiver, theGuard) = receiverFor();
        var (gate, entered) = parkHandler();

        var originalListener = new RecordingListener();
        var redeliveryListener = new RecordingListener();
        var original = ping("succeeds");

        await receiver.ReceivedAsync(originalListener, original);
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        await receiver.ReceivedAsync(redeliveryListener, ping("succeeds", original.Id));

        gate.SetResult();

        // Each delivery settles against its OWN listener: the redelivery's handle (a new RabbitMQ delivery tag,
        // the newest SQS receipt handle, a fresh Service Bus lock) is the live one
        await waitUntil(() => originalListener.Completed.Count == 1 && redeliveryListener.Completed.Count == 1);
        redeliveryListener.Deferred.ShouldBeEmpty();

        NativeAckPingHandler.Handled.Count(x => x == "succeeds").ShouldBe(1);
        theGuard.HeldCount.ShouldBe(0);
    }

    [Fact]
    public async Task an_already_processed_redelivery_is_acked_immediately()
    {
        var (receiver, _) = receiverFor();

        var originalListener = new RecordingListener();
        var original = ping("processed");
        await receiver.ReceivedAsync(originalListener, original);
        await waitUntil(() => originalListener.Completed.Count == 1);

        var redeliveryListener = new RecordingListener();
        await receiver.ReceivedAsync(redeliveryListener, ping("processed", original.Id));

        await waitUntil(() => redeliveryListener.Completed.Count == 1);
        NativeAckPingHandler.Handled.Count(x => x == "processed").ShouldBe(1);
    }

    [Fact]
    public async Task the_held_redelivery_is_handed_back_when_the_original_is_not_settled()
    {
        // One lane, so the second message waits behind the parked first one instead of starting alongside it
        var (receiver, theGuard) = receiverFor(parallelism: 1);
        var (_, entered) = parkHandler();

        var originalListener = new RecordingListener();
        var redeliveryListener = new RecordingListener();
        var original = ping("fails");

        // The original is admitted but its handler never finishes...
        await receiver.ReceivedAsync(originalListener, original);
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // ...a second copy queues behind it in the lane, and a redelivery of that second copy's message is held
        var queued = ping("queued");
        await receiver.ReceivedAsync(originalListener, queued);
        await receiver.ReceivedAsync(redeliveryListener, ping("queued", queued.Id));
        theGuard.HeldCount.ShouldBe(1);

        // The receiver is latched before the queued copy runs, so the queued copy is handed back to the broker
        // rather than settled -- and so is the held redelivery, which may be the only live copy now
        receiver.Latch();
        NativeAckPingHandler.Gate!.SetResult();

        await waitUntil(() => redeliveryListener.Deferred.Count == 1);
        redeliveryListener.Completed.ShouldBeEmpty();
        theGuard.HeldCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_node_that_drains_mid_flight_never_settles_the_held_redelivery()
    {
        var (receiver, theGuard) = receiverFor();
        var (_, entered) = parkHandler();

        var originalListener = new RecordingListener();
        var redeliveryListener = new RecordingListener();
        var original = ping("drained");

        await receiver.ReceivedAsync(originalListener, original);
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        await receiver.ReceivedAsync(redeliveryListener, ping("drained", original.Id));

        // An unlatched drain does not wait on the parked handler -- exactly the Pulsar shape: the node goes away
        // with the original still parked and the redelivery still held
        await receiver.DrainAsync();

        // The broker still owns the message, so the next node gets it
        redeliveryListener.Completed.ShouldBeEmpty();
        redeliveryListener.Deferred.ShouldBeEmpty();
        originalListener.Completed.ShouldBeEmpty();
        theGuard.HeldCount.ShouldBe(1);
    }

    [Fact]
    public async Task the_held_redelivery_takes_over_the_lease()
    {
        var endpoint = new LeasedStubEndpoint("leased-5533", new StubTransport())
        {
            InMemoryIdempotency = new InMemoryIdempotencySettings()
        };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var receiver = new NativeAckReceiver(endpoint, theRuntime,
            new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));
        var (gate, entered) = parkHandler();

        var originalListener = new FakeLeaseListener();
        var redeliveryListener = new FakeLeaseListener();
        var original = ping("leased");

        await receiver.ReceivedAsync(originalListener, original);
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var redelivery = ping("leased", original.Id);
        await receiver.ReceivedAsync(redeliveryListener, redelivery);

        // SQS: the newest receipt handle is the valid one, so it is the redelivery's visibility that gets extended
        await receiver.Leases!.TickAsync(TestContext.Current.CancellationToken);
        redeliveryListener.Calls.SelectMany(x => x).ShouldContain(redelivery);

        gate.SetResult();

        await waitUntil(() => redeliveryListener.Completed.Count == 1);
        await waitUntil(() => receiver.Leases!.InFlightCount == 0);
    }
}
