using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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

#region test message + handler

public record LeasePing(string Name);

public class LeasePingHandler
{
    public static readonly ConcurrentBag<string> Handled = new();

    /// <summary>Gate so a test can hold a lane open without Task.Delay.</summary>
    public static TaskCompletionSource? Gate;

    /// <summary>Signalled the moment a handler actually starts, so a test never has to guess.</summary>
    public static TaskCompletionSource? Entered;

    public async Task Handle(LeasePing message)
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
///     GH-4048. A NativeAck delivery is held unsettled for lane queue time PLUS handler time, and on SQS / ASB /
///     Pub/Sub / JetStream the broker runs a clock on it the whole time. These cover the tracker that keeps that
///     clock alive and -- the part that actually matters -- what happens when it is lost anyway.
/// </summary>
public class lease_renewal_4048 : IAsyncLifetime
{
    private IHost _host = null!;
    private IWolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<LeasePingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = _host.Services.GetRequiredService<IWolverineRuntime>();
        LeasePingHandler.Handled.Clear();
        LeasePingHandler.Gate = null;
        LeasePingHandler.Entered = null;
    }

    public async ValueTask DisposeAsync()
    {
        LeasePingHandler.Gate = null;
        LeasePingHandler.Entered = null;
        await _host.StopAsync();
        _host.Dispose();
    }

    private LeasedStubEndpoint endpointFor(Action<Endpoint>? configure = null)
    {
        var endpoint = new LeasedStubEndpoint("leased", new StubTransport());
        configure?.Invoke(endpoint);
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);
        return endpoint;
    }

    private NativeAckReceiver receiverFor(Action<Endpoint>? configure = null, IHandlerPipeline? pipeline = null)
    {
        var endpoint = endpointFor(configure);

        return new NativeAckReceiver(endpoint, theRuntime,
            pipeline ?? new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));
    }

    private static Envelope pingEnvelope(string name)
    {
        return new Envelope(new LeasePing(name)) { MessageType = typeof(LeasePing).ToMessageTypeName() };
    }

    private static LeaseRenewalTracker trackerFor(FakeLeaseListener listener)
    {
        // An hour-long interval so the loop never fires on its own -- every test drives TickAsync explicitly
        return new LeaseRenewalTracker(listener, listener.Address, NullLogger.Instance, null,
            CancellationToken.None, 1.Hours());
    }

    private static Envelope tracked(LeaseRenewalTracker tracker, FakeLeaseListener listener, string name,
        DateTimeOffset? receivedAt = null)
    {
        var envelope = pingEnvelope(name);
        envelope.Listener = listener;
        envelope.ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow;
        tracker.Track(envelope);
        return envelope;
    }

    #region scheduling

    [Fact]
    public async Task ticks_at_half_the_lease()
    {
        var listener = new FakeLeaseListener { LeaseDuration = 40.Seconds() };
        await using var tracker = new LeaseRenewalTracker(listener, listener.Address, NullLogger.Instance, null,
            CancellationToken.None);

        tracker.Interval.ShouldBe(20.Seconds());
    }

    [Fact]
    public async Task the_tick_interval_is_floored_at_one_second()
    {
        var listener = new FakeLeaseListener { LeaseDuration = 400.Milliseconds() };
        await using var tracker = new LeaseRenewalTracker(listener, listener.Address, NullLogger.Instance, null,
            CancellationToken.None);

        tracker.Interval.ShouldBe(1.Seconds());
    }

    [Fact]
    public async Task nothing_is_sent_while_nothing_is_in_flight()
    {
        var listener = new FakeLeaseListener();
        await using var tracker = trackerFor(listener);

        await tracker.TickAsync(CancellationToken.None);

        listener.Calls.ShouldBeEmpty();
    }

    /// <summary>
    ///     A receiver can serve several listeners (ListenerCount > 1, per-tenant compound listeners), and a renewal
    ///     -- exactly like a settle -- has to go to the listener the delivery actually arrived on.
    /// </summary>
    [Fact]
    public async Task renewals_are_grouped_by_the_listener_that_delivered_the_envelope()
    {
        var a = new FakeLeaseListener { Address = new Uri("stub://a") };
        var b = new FakeLeaseListener { Address = new Uri("stub://b") };
        await using var tracker = trackerFor(a);

        var one = tracked(tracker, a, "one");
        var two = tracked(tracker, a, "two");
        var three = tracked(tracker, b, "three");

        await tracker.TickAsync(CancellationToken.None);

        a.Calls.Count.ShouldBe(1);
        a.Calls.Single().Select(x => x.Id).OrderBy(x => x)
            .ShouldBe(new[] { one.Id, two.Id }.OrderBy(x => x));

        b.Calls.Count.ShouldBe(1);
        b.Calls.Single().Single().Id.ShouldBe(three.Id);
    }

    [Fact]
    public async Task no_renewal_calls_at_all_when_the_client_renews_for_us()
    {
        var listener = new FakeLeaseListener { RequiresExplicitRenewal = false };
        await using var tracker = trackerFor(listener);

        tracked(tracker, listener, "one");

        await tracker.TickAsync(CancellationToken.None);

        // Pub/Sub's SubscriberClient shape: Wolverine enforces only the ceiling
        listener.Calls.ShouldBeEmpty();
        tracker.InFlightCount.ShouldBe(1);
        tracker.LeasesLostBeforeStarting.ShouldBe(0);
    }

    #endregion

    #region the ceiling

    [Fact]
    public async Task stops_renewing_before_an_extension_would_cross_the_maximum()
    {
        var listener = new FakeLeaseListener
        {
            LeaseDuration = 30.Seconds(),
            MaximumLeaseExtension = 60.Seconds()
        };
        await using var tracker = trackerFor(listener);

        // 40s old: 40 + 30 > 60, so the next extension would carry it past the ceiling
        tracked(tracker, listener, "old", DateTimeOffset.UtcNow.Subtract(40.Seconds()));
        tracked(tracker, listener, "young", DateTimeOffset.UtcNow.Subtract(5.Seconds()));

        await tracker.TickAsync(CancellationToken.None);

        tracker.CeilingsReached.ShouldBe(1);
        tracker.InFlightCount.ShouldBe(1);
        listener.Calls.Single().Single().Message.ShouldBeOfType<LeasePing>().Name.ShouldBe("young");
    }

    /// <summary>
    ///     The ceiling is NOT a lost lease. The delivery may still finish inside the lease it already holds, so
    ///     the envelope keeps running and is not dropped from the lane.
    /// </summary>
    [Fact]
    public async Task reaching_the_ceiling_is_not_treated_as_a_lost_lease()
    {
        var listener = new FakeLeaseListener
        {
            LeaseDuration = 30.Seconds(),
            MaximumLeaseExtension = 60.Seconds()
        };
        await using var tracker = trackerFor(listener);

        var envelope = tracked(tracker, listener, "old", DateTimeOffset.UtcNow.Subtract(40.Seconds()));

        await tracker.TickAsync(CancellationToken.None);

        tracker.LeasesLostBeforeStarting.ShouldBe(0);
        tracker.LeasesLostWhileExecuting.ShouldBe(0);
        tracker.WasLeaseLost(envelope).ShouldBeFalse();
        tracker.TryBeginExecution(envelope).ShouldBeTrue();
    }

    #endregion

    #region loss classification

    [Fact]
    public async Task an_envelope_the_broker_refuses_to_renew_has_lost_its_lease()
    {
        var listener = new FakeLeaseListener();
        await using var tracker = trackerFor(listener);

        var refused = tracked(tracker, listener, "refused");
        var kept = tracked(tracker, listener, "kept");
        listener.RefuseWhen = e => e.Id == refused.Id;

        await tracker.TickAsync(CancellationToken.None);

        tracker.WasLeaseLost(refused).ShouldBeTrue();
        tracker.WasLeaseLost(kept).ShouldBeFalse();
        tracker.LeasesLostBeforeStarting.ShouldBe(1);
        tracker.LeasesRenewed.ShouldBe(1);
        tracker.InFlightCount.ShouldBe(1);
    }

    /// <summary>
    ///     A thrown renewal is transient -- network, throttle. Treating it as a lost lease would drop live work
    ///     on a blip.
    /// </summary>
    [Fact]
    public async Task a_thrown_renewal_is_retried_rather_than_treated_as_a_loss()
    {
        var listener = new FakeLeaseListener { LeaseDuration = 30.Seconds() };
        await using var tracker = trackerFor(listener);

        var envelope = tracked(tracker, listener, "one");
        listener.Throw = true;

        await tracker.TickAsync(CancellationToken.None);

        tracker.WasLeaseLost(envelope).ShouldBeFalse();
        tracker.InFlightCount.ShouldBe(1);
        tracker.LeasesLostBeforeStarting.ShouldBe(0);

        listener.Throw = false;
        await tracker.TickAsync(CancellationToken.None);

        tracker.LeasesRenewed.ShouldBe(1);
        tracker.WasLeaseLost(envelope).ShouldBeFalse();
    }

    /// <summary>
    ///     The second detector, and the only one available on a transport whose renewal call cannot report a
    ///     per-message failure -- JetStream's AckProgressAsync is fire-and-forget without double-ack.
    /// </summary>
    [Fact]
    public async Task loss_is_inferred_once_a_full_lease_passes_with_no_successful_renewal()
    {
        var listener = new FakeLeaseListener { LeaseDuration = 2.Seconds() };
        await using var tracker = trackerFor(listener);

        // Received far enough in the past that no renewal has ever succeeded inside a lease duration
        var envelope = tracked(tracker, listener, "one", DateTimeOffset.UtcNow.Subtract(10.Seconds()));
        listener.Throw = true;

        await tracker.TickAsync(CancellationToken.None);

        tracker.WasLeaseLost(envelope).ShouldBeTrue();
        tracker.LeasesLostBeforeStarting.ShouldBe(1);
    }

    [Fact]
    public async Task a_successful_renewal_resets_the_inferred_loss_clock()
    {
        var listener = new FakeLeaseListener { LeaseDuration = 2.Seconds() };
        await using var tracker = trackerFor(listener);

        var envelope = tracked(tracker, listener, "one", DateTimeOffset.UtcNow.Subtract(10.Seconds()));

        // Same stale envelope as the test above, but the renewal LANDS this time
        await tracker.TickAsync(CancellationToken.None);

        tracker.WasLeaseLost(envelope).ShouldBeFalse();
        tracker.LeasesRenewed.ShouldBe(1);
    }

    [Fact]
    public async Task a_lease_lost_while_executing_is_metered_separately()
    {
        var listener = new FakeLeaseListener();
        await using var tracker = trackerFor(listener);

        var envelope = tracked(tracker, listener, "running");
        tracker.TryBeginExecution(envelope).ShouldBeTrue();
        listener.RefuseWhen = e => e.Id == envelope.Id;

        await tracker.TickAsync(CancellationToken.None);

        // Realized duplication, not prevented duplication -- the two counters mean different things
        tracker.LeasesLostWhileExecuting.ShouldBe(1);
        tracker.LeasesLostBeforeStarting.ShouldBe(0);
        tracker.WasLeaseLost(envelope).ShouldBeTrue();
    }

    [Fact]
    public async Task untracking_clears_both_the_in_flight_and_the_lost_state()
    {
        var listener = new FakeLeaseListener();
        await using var tracker = trackerFor(listener);

        var envelope = tracked(tracker, listener, "one");
        listener.RefuseWhen = e => e.Id == envelope.Id;
        await tracker.TickAsync(CancellationToken.None);

        tracker.WasLeaseLost(envelope).ShouldBeTrue();

        tracker.Untrack(envelope);

        tracker.WasLeaseLost(envelope).ShouldBeFalse();
        tracker.InFlightCount.ShouldBe(0);
        tracker.LostCount.ShouldBe(0);
    }

    #endregion

    #region enforcement in the lane -- the assertions that matter

    /// <summary>
    ///     The single most important assertion in GH-4048. A lease-lost envelope that has not started executing is
    ///     DROPPED: no Complete, and critically no Defer. Every transport's defer path is settle-then-republish,
    ///     so deferring after the lease is gone publishes a second copy on top of the redelivery the broker is
    ///     already performing.
    /// </summary>
    [Fact]
    public async Task a_lease_lost_envelope_that_has_not_started_is_dropped_without_completing_or_deferring()
    {
        var receiver = receiverFor(e => e.MaxDegreeOfParallelism = 1);
        var listener = new FakeLeaseListener();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LeasePingHandler.Gate = gate;
        LeasePingHandler.Entered = entered;

        // "first" occupies the single lane; "second" is stuck behind it, which is exactly the state this issue
        // exists for -- the risk window is lane queue time, not handler time.
        var first = pingEnvelope("first");
        var second = pingEnvelope("second");
        await receiver.ReceivedAsync(listener, first);
        await receiver.ReceivedAsync(listener, second);

        receiver.Leases.ShouldNotBeNull();
        var tracker = receiver.Leases!;
        await entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        await waitUntil(() => tracker.InFlightCount == 2);

        listener.RefuseWhen = e => e.Id == second.Id;
        await tracker.TickAsync(CancellationToken.None);

        tracker.WasLeaseLost(second).ShouldBeTrue();
        tracker.LeasesLostBeforeStarting.ShouldBe(1);

        gate.SetResult();
        await waitUntil(() => listener.Completed.Count == 1);
        await waitUntil(() => tracker.LostCount == 0 && tracker.InFlightCount == 0);

        // "first" settled normally
        LeasePingHandler.Handled.ShouldContain("first");
        listener.Completed.Single().Id.ShouldBe(first.Id);

        // "second" was never handled, never completed, and -- the whole point -- never deferred
        LeasePingHandler.Handled.ShouldNotContain("second");
        listener.Deferred.ShouldBeEmpty();
    }

    /// <summary>
    ///     A running handler cannot be un-run, so this envelope stays at-least-once. What the design does do is
    ///     stop compounding it: the receiver's own failure-path defer is suppressed, because deferring would
    ///     republish on top of the redelivery the broker has already started.
    /// </summary>
    [Fact]
    public async Task a_pipeline_failure_after_the_lease_is_lost_mid_execution_is_not_deferred()
    {
        var pipeline = new GatedThrowingPipeline();
        var receiver = receiverFor(pipeline: pipeline);
        var listener = new FakeLeaseListener();

        var envelope = pingEnvelope("running");
        await receiver.ReceivedAsync(listener, envelope);

        receiver.Leases.ShouldNotBeNull();
        var tracker = receiver.Leases!;

        // The pipeline is on the stack, so this envelope is genuinely executing
        await pipeline.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        listener.RefuseWhen = e => e.Id == envelope.Id;
        await tracker.TickAsync(CancellationToken.None);
        tracker.LeasesLostWhileExecuting.ShouldBe(1);
        tracker.LeasesLostBeforeStarting.ShouldBe(0);

        pipeline.Gate.SetResult();
        await waitUntil(() => tracker.InFlightCount == 0 && tracker.LostCount == 0);

        listener.Deferred.ShouldBeEmpty();
    }

    /// <summary>
    ///     The control for the test above: with the lease intact, the very same pipeline failure DOES defer. The
    ///     pair is the test -- a green "no defer" alone would also pass if the receiver simply never deferred.
    /// </summary>
    [Fact]
    public async Task a_pipeline_failure_with_an_intact_lease_still_defers()
    {
        var pipeline = new GatedThrowingPipeline();
        var receiver = receiverFor(pipeline: pipeline);
        var listener = new FakeLeaseListener();

        await receiver.ReceivedAsync(listener, pingEnvelope("running"));
        await pipeline.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        pipeline.Gate.SetResult();
        await waitUntil(() => listener.Deferred.Count == 1);

        listener.Completed.ShouldBeEmpty();
    }

    /// <summary>
    ///     The counterpart. A LATCHED receiver still holds the lease, so handing the delivery back is correct --
    ///     the lost-lease branch sits right next to it and must not change this.
    /// </summary>
    [Fact]
    public async Task a_latched_receiver_still_defers_because_the_lease_is_intact()
    {
        var receiver = receiverFor();
        var listener = new FakeLeaseListener();

        receiver.Latch();
        await receiver.ReceivedAsync(listener, pingEnvelope("latched"));
        await receiver.DrainAsync();

        listener.Deferred.Count.ShouldBe(1);
        listener.Completed.Count.ShouldBe(0);
    }

    [Fact]
    public async Task an_intact_lease_settles_normally_and_stops_being_tracked()
    {
        var receiver = receiverFor();
        var listener = new FakeLeaseListener();

        await receiver.ReceivedAsync(listener, pingEnvelope("fine"));

        receiver.Leases.ShouldNotBeNull();
        var tracker = receiver.Leases!;

        await waitUntil(() => listener.Completed.Count == 1);
        await waitUntil(() => tracker.InFlightCount == 0);

        LeasePingHandler.Handled.ShouldContain("fine");
        listener.Deferred.ShouldBeEmpty();
        tracker.LostCount.ShouldBe(0);
    }

    [Fact]
    public async Task no_tracker_is_built_for_a_listener_that_cannot_renew()
    {
        var receiver = receiverFor();
        var listener = new PlainListener();

        await receiver.ReceivedAsync(listener, pingEnvelope("plain"));
        await waitUntil(() => listener.Completed.Count == 1);

        // RabbitMQ / Redis Streams: an unsettled delivery never expires, so there is no clock and no loop
        receiver.Leases.ShouldBeNull();
    }

    #endregion

    #region the startup contract

    /// <summary>
    ///     Without this check, opting a clocked transport into NativeAck and forgetting renewal produces a silent
    ///     duplicate generator. With it, the mistake is a startup exception.
    /// </summary>
    [Fact]
    public async Task a_native_ack_endpoint_on_a_clocked_transport_needs_a_lease_renewing_listener()
    {
        var endpoint = endpointFor(e => e.IsListener = true);
        var agent = new ListeningAgent(endpoint, (WolverineRuntime)theRuntime);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await agent.StartAsync());

        ex.Message.ShouldContain(nameof(ISupportLeaseRenewal));
        ex.Message.ShouldContain(endpoint.Uri.ToString());

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task a_native_ack_endpoint_whose_listener_can_renew_starts_normally()
    {
        var endpoint = new LeaseRenewingStubEndpoint("renewing", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, (WolverineRuntime)theRuntime);
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);

        await agent.DisposeAsync();
    }

    /// <summary>
    ///     The check is scoped to NativeAck. Inline settles inside the callback and Buffered/Durable settle at
    ///     receipt, so none of them holds a delivery long enough to need a renewal -- requiring one there would
    ///     break every existing SQS endpoint.
    /// </summary>
    [Fact]
    public async Task the_contract_check_only_applies_to_native_ack()
    {
        var endpoint = new LeasedStubEndpoint("inline", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.Inline;
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, (WolverineRuntime)theRuntime);
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);

        await agent.DisposeAsync();
    }

    /// <summary>
    ///     And it is scoped to transports that actually expire an unsettled delivery. RabbitMQ has no clock, so a
    ///     NativeAck Rabbit listener has nothing to renew.
    /// </summary>
    [Fact]
    public async Task the_contract_check_does_not_apply_to_a_transport_with_no_clock()
    {
        var endpoint = new UnclockedStubEndpoint("unclocked", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, (WolverineRuntime)theRuntime);
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);

        await agent.DisposeAsync();
    }

    #endregion

    private static async Task waitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}

/// <summary>GH-4048. A clocked transport that has opted into NativeAck, standing in for SQS.</summary>
internal class LeasedStubEndpoint : StubEndpoint
{
    public LeasedStubEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
    {
    }

    protected override bool supportsNativeAck => true;

    protected internal override bool holdsExpiringLease => true;
}

/// <summary>A clocked transport whose listener honours the contract.</summary>
internal class LeaseRenewingStubEndpoint : LeasedStubEndpoint, ISupportLeaseRenewal
{
    public LeaseRenewingStubEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
    {
    }

    public TimeSpan LeaseDuration => 30.Seconds();
    public TimeSpan MaximumLeaseExtension => 12.Hours();

    public ValueTask<IReadOnlyList<Envelope>> RenewLeasesAsync(IReadOnlyList<Envelope> envelopes,
        CancellationToken token)
    {
        return ValueTask.FromResult<IReadOnlyList<Envelope>>([]);
    }
}

/// <summary>A NativeAck transport with no clock at all -- RabbitMQ, Redis Streams.</summary>
internal class UnclockedStubEndpoint : StubEndpoint
{
    public UnclockedStubEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
    {
    }

    protected override bool supportsNativeAck => true;
}

/// <summary>A listener whose broker puts a clock on an unsettled delivery, with the clock under test control.</summary>
internal class FakeLeaseListener : IListener, ISupportLeaseRenewal
{
    public ConcurrentBag<Envelope> Completed { get; } = new();
    public ConcurrentBag<Envelope> Deferred { get; } = new();

    /// <summary>Every batch this listener has been asked to renew, in the groups the tracker built.</summary>
    public ConcurrentBag<IReadOnlyList<Envelope>> Calls { get; } = new();

    /// <summary>Which envelopes the "broker" will refuse to renew.</summary>
    public Func<Envelope, bool> RefuseWhen { get; set; } = _ => false;

    /// <summary>Make the renewal call fail transiently.</summary>
    public bool Throw { get; set; }

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumLeaseExtension { get; set; } = TimeSpan.FromHours(12);
    public bool RequiresExplicitRenewal { get; set; } = true;

    public Uri Address { get; set; } = new("stub://leased");
    public IHandlerPipeline? Pipeline => null;

    public ValueTask<IReadOnlyList<Envelope>> RenewLeasesAsync(IReadOnlyList<Envelope> envelopes,
        CancellationToken token)
    {
        Calls.Add(envelopes.ToArray());

        if (Throw)
        {
            throw new TimeoutException("the broker is not answering");
        }

        IReadOnlyList<Envelope> refused = envelopes.Where(RefuseWhen).ToArray();
        return ValueTask.FromResult(refused);
    }

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
}

/// <summary>A listener on a broker with no clock at all.</summary>
internal class PlainListener : IListener
{
    public ConcurrentBag<Envelope> Completed { get; } = new();
    public ConcurrentBag<Envelope> Deferred { get; } = new();

    public Uri Address { get; } = new("stub://plain");
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
}

/// <summary>
///     Holds the pipeline open on a gate and then throws OUT of InvokeAsync, which is the one failure the
///     native-ack receiver handles itself (everything else is settled by the pipeline's own continuations).
/// </summary>
internal class GatedThrowingPipeline : IHandlerPipeline
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InvokeAsync(Envelope envelope, IChannelCallback channel)
    {
        Entered.TrySetResult();
        await Gate.Task;
        throw new DivideByZeroException("boom");
    }

    public Task InvokeAsync(Envelope envelope, IChannelCallback channel, Activity activity)
    {
        return InvokeAsync(envelope, channel);
    }

    public ValueTask<IContinuation> TryDeserializeEnvelope(Envelope envelope)
    {
        throw new NotSupportedException();
    }
}
