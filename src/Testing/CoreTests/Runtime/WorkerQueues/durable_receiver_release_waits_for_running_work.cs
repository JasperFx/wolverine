using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// F15 and F16 from the durable compliance rig. Both hand a running handler's inbox row back to the cluster while
/// it is still running, so another node claims it and runs the same message alongside.
///
/// <para>F15: a pause raised from inside the handler pipeline (rate limiting, <c>AndPauseProcessing</c>) drains the
/// receiver without latching it first, because the execution that raised the pause is on the call stack and cannot
/// be waited for. The drain then released every row at the address straight away, underneath every sibling handler
/// still running. GH-4797 had already fixed the same thing for the latched drain.</para>
///
/// <para>F16: a listener builds a new receiver on every restart, and the new receiver's
/// <c>ClearInFlightIncomingAsync</c> released every row at the address -- including the rows the previous receiver
/// was still holding for a handler GH-4797 had deliberately waited out.</para>
/// </summary>
public class durable_receiver_release_waits_for_running_work : IAsyncLifetime
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly StubEndpoint theEndpoint = new("ownership", new StubTransport());

    private readonly TaskCompletionSource _handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<DurableReceiver> _receivers = new();

    public durable_receiver_release_waits_for_running_work()
    {
        theRuntime.DurabilitySettings.DrainTimeout = 250.Milliseconds();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _releaseHandler.TrySetResult();
        foreach (var receiver in _receivers) receiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    private DurableReceiver buildReceiver()
    {
        var receiver = new DurableReceiver(theEndpoint, theRuntime, thePipeline);
        _receivers.Add(receiver);
        return receiver;
    }

    private void theHandlerRunsUntilReleased()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(_ =>
            {
                _handlerEntered.TrySetResult();
                return _releaseHandler.Task;
            });
    }

    private async Task aHandlerIsRunningOn(DurableReceiver receiver)
    {
        var envelope = ObjectMother.Envelope();
        envelope.WasPersistedInInbox = true;
        await receiver.EnqueueAsync(envelope);

        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    private bool theAddressWasReleased() => theRuntime.Storage.Inbox.ReceivedCalls()
        .Any(x => x.GetMethodInfo().Name == nameof(IMessageInbox.ReleaseIncomingAsync));

    private static async Task waitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException(message);
    }

    [Fact]
    public async Task an_unlatched_drain_does_not_release_rows_while_a_handler_is_still_running()
    {
        theHandlerRunsUntilReleased();
        var receiver = buildReceiver();
        await aHandlerIsRunningOn(receiver);

        // No Latch(): the shape of a pause raised from inside the pipeline
        await receiver.DrainAsync();

        theAddressWasReleased().ShouldBeFalse(
            "The drain handed the running handler's row to any node while it was still running");

        // And the rows are handed back once the handler finishes
        _releaseHandler.TrySetResult();
        await waitForAsync(theAddressWasReleased, "The held rows were never released after the handler finished");
    }

    [Fact]
    public async Task a_drain_raised_from_inside_the_pipeline_neither_deadlocks_nor_releases_its_siblings()
    {
        var sibling = ObjectMother.Envelope();
        var pausing = ObjectMother.Envelope();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DurableReceiver receiver = null!;

        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(async call =>
            {
                var envelope = call.Arg<Envelope>();
                if (envelope == sibling)
                {
                    _handlerEntered.TrySetResult();
                    await _releaseHandler.Task;
                    return;
                }

                // The pause: drains the receiver from inside its own execution, without waiting on itself
                await receiver.DrainAsync();
                drained.TrySetResult();
            });

        receiver = buildReceiver();
        await aHandlerIsRunningOnSibling(receiver, sibling);
        await receiver.EnqueueAsync(pausing);

        await drained.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        theAddressWasReleased().ShouldBeFalse(
            "The pause released its sibling's row while the sibling was still running");

        _releaseHandler.TrySetResult();
        await waitForAsync(theAddressWasReleased, "The held rows were never released after the sibling finished");
    }

    private async Task aHandlerIsRunningOnSibling(DurableReceiver receiver, Envelope sibling)
    {
        sibling.WasPersistedInInbox = true;
        await receiver.EnqueueAsync(sibling);
        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_rebuilt_receiver_does_not_release_rows_the_previous_one_is_still_running()
    {
        theHandlerRunsUntilReleased();
        var previous = buildReceiver();
        await aHandlerIsRunningOn(previous);

        // A forced restart: latch, drain (which times out and holds the rows, GH-4797), then build again
        previous.Latch();
        await previous.DrainAsync();

        var rebuilt = buildReceiver();
        await rebuilt.ClearInFlightIncomingAsync();

        theAddressWasReleased().ShouldBeFalse(
            "The rebuilt receiver released the row of a handler the previous receiver is still running");
    }

    [Fact]
    public async Task the_previous_receiver_does_not_release_the_rows_of_a_rebuilt_one_that_is_receiving()
    {
        theHandlerRunsUntilReleased();
        var previous = buildReceiver();
        await aHandlerIsRunningOn(previous);

        previous.Latch();
        await previous.DrainAsync();

        // The rebuilt receiver is live and has work of its own at the same address
        var rebuilt = buildReceiver();
        await rebuilt.ClearInFlightIncomingAsync();
        await rebuilt.EnqueueAsync(ObjectMother.Envelope());

        // The old handler finishes, and the old receiver's held release runs
        _releaseHandler.TrySetResult();
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        theAddressWasReleased().ShouldBeFalse(
            "The previous receiver's release handed back every row at the address, including the rebuilt receiver's");
    }

    [Fact]
    public async Task an_envelope_skipped_by_the_latch_is_handed_back_by_id_when_the_address_is_busy()
    {
        theHandlerRunsUntilReleased();
        var previous = buildReceiver();
        await aHandlerIsRunningOn(previous);

        // Posted after the latch, so the receiver skips it: it never runs here, and its row is still owned here
        var queued = ObjectMother.Envelope();
        queued.Destination = theEndpoint.Uri;
        previous.Latch();
        await previous.EnqueueAsync(queued);
        await previous.DrainAsync();

        var rebuilt = buildReceiver();
        await rebuilt.EnqueueAsync(ObjectMother.Envelope());

        _releaseHandler.TrySetResult();

        await waitForAsync(
            () => theRuntime.Storage.ReceivedCalls().Any(x =>
                x.GetMethodInfo().Name == nameof(IMessageStore.ReassignIncomingAsync) &&
                x.GetArguments()[0] is int owner && owner == TransportConstants.AnyNode &&
                ((IReadOnlyList<Envelope>)x.GetArguments()[1]!).Contains(queued)),
            "The abandoned envelope's row was never handed back");

        theAddressWasReleased().ShouldBeFalse();
    }

    [Fact]
    public async Task a_receiver_with_no_previous_one_still_clears_the_address_on_start()
    {
        // The control: the first receiver at an address keeps the startup sweep
        var receiver = buildReceiver();
        await receiver.ClearInFlightIncomingAsync();

        theAddressWasReleased().ShouldBeTrue();
    }

    [Fact]
    public async Task a_rebuilt_receiver_clears_the_address_once_the_previous_one_is_idle()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);

        var previous = buildReceiver();
        await previous.EnqueueAsync(ObjectMother.Envelope());
        previous.Latch();
        await previous.DrainAsync();
        theRuntime.Storage.Inbox.ClearReceivedCalls();

        var rebuilt = buildReceiver();
        await rebuilt.ClearInFlightIncomingAsync();

        theAddressWasReleased().ShouldBeTrue();
    }
}
