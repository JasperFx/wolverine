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
/// F18. The InboxStaleTime heartbeat timer was started with the receiver and only ever stopped by DisposeAsync, which
/// also threw away the batch members the receiver was still holding. The synchronous Dispose() never stopped it at
/// all, so a disposed or simply idle receiver ticked for the life of the process. The heartbeat now runs while the
/// receiver holds something -- including batch members waiting on a batch after the receiver itself is gone -- and
/// stops once it holds nothing.
/// </summary>
public class the_inbox_heartbeat_runs_only_while_something_is_held : IAsyncLifetime
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IMessageInbox theInbox = Substitute.For<IMessageInbox, IMessageInboxHeartbeat>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly IHandlerPipeline theBatchingPipeline = Substitute.For<IHandlerPipeline>();
    private readonly StubEndpoint theEndpoint = new("heartbeat", new StubTransport());
    private readonly StubEndpoint theBatchEndpoint = new("heartbeat-batches", new StubTransport());
    private readonly List<DurableReceiver> _receivers = new();

    private readonly TaskCompletionSource _handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public the_inbox_heartbeat_runs_only_while_something_is_held()
    {
        theRuntime.Storage.Inbox.Returns(theInbox);

        // A heartbeat period of 100ms
        theRuntime.DurabilitySettings.InboxStaleTime = 300.Milliseconds();

        // The batching pipeline accepts the envelope as a batch member and returns
        theBatchingPipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>())
            .Returns(call =>
            {
                call.Arg<Envelope>().InBatch = true;
                return Task.CompletedTask;
            });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _releaseHandler.TrySetResult();
        foreach (var receiver in _receivers) receiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    private DurableReceiver buildReceiver(IHandlerPipeline pipeline, StubEndpoint? endpoint = null)
    {
        var receiver = new DurableReceiver(endpoint ?? theEndpoint, theRuntime, pipeline);
        _receivers.Add(receiver);
        return receiver;
    }

    private static Envelope anEnvelopeAt(StubEndpoint endpoint)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = endpoint.Uri;
        envelope.WasPersistedInInbox = true;
        return envelope;
    }

    private bool theClockWasRestartedFor(Envelope envelope) => ((IMessageInboxHeartbeat)theInbox).ReceivedCalls()
        .Select(x => (IReadOnlyList<Envelope>)x.GetArguments()[1]!)
        .Any(x => x.Contains(envelope));

    private static async Task waitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25.Milliseconds());
        }

        throw new TimeoutException(message);
    }

    [Fact]
    public async Task the_heartbeat_stops_once_the_receiver_holds_nothing()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(_ =>
        {
            _handlerEntered.TrySetResult();
            return _releaseHandler.Task;
        });

        var receiver = buildReceiver(thePipeline);
        receiver.HasInboxHeartbeat.ShouldBeTrue();

        var envelope = anEnvelopeAt(theEndpoint);
        await receiver.EnqueueAsync(envelope);
        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Running for longer than a period: its row's clock is restarted
        await waitForAsync(() => theClockWasRestartedFor(envelope), "The running envelope's clock was never restarted");
        receiver.HeartbeatIsRunning.ShouldBeTrue();

        _releaseHandler.TrySetResult();

        await waitForAsync(() => !receiver.HeartbeatIsRunning,
            "The heartbeat kept running after the receiver stopped holding anything");
    }

    [Fact]
    public async Task an_idle_receiver_starts_the_heartbeat_again_when_it_takes_on_work()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(_ =>
        {
            _handlerEntered.TrySetResult();
            return _releaseHandler.Task;
        });

        var receiver = buildReceiver(thePipeline);
        await waitForAsync(() => !receiver.HeartbeatIsRunning, "An idle receiver's heartbeat was running");

        var envelope = anEnvelopeAt(theEndpoint);
        await receiver.EnqueueAsync(envelope);
        await _handlerEntered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        await waitForAsync(() => theClockWasRestartedFor(envelope),
            "The heartbeat did not start again for an envelope taken on after the receiver went idle");
    }

    [Fact]
    public async Task dispose_stops_the_heartbeat_of_a_receiver_holding_nothing()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);

        var receiver = buildReceiver(thePipeline);
        await receiver.EnqueueAsync(anEnvelopeAt(theEndpoint));
        await waitForAsync(() => receiver.QueueCount == 0, "The envelope never ran");

        // The synchronous Dispose() is the one a listener uses on stop
#pragma warning disable VSTHRD103
        receiver.Dispose();
#pragma warning restore VSTHRD103

        await waitForAsync(() => !receiver.HeartbeatIsRunning,
            "The synchronous Dispose() left the heartbeat timer running");
    }

    [Fact]
    public async Task dispose_async_keeps_the_heartbeat_for_batch_members_until_their_batch_settles()
    {
        var itemReceiver = buildReceiver(theBatchingPipeline);
        var batchReceiver = buildReceiver(thePipeline, theBatchEndpoint);

        var members = new[] { anEnvelopeAt(theEndpoint), anEnvelopeAt(theEndpoint) };
        foreach (var member in members) await itemReceiver.EnqueueAsync(member);
        await waitForAsync(() => itemReceiver.HeldBatchMemberCount == 2,
            "The members were never held for their batch");

        // The receiver the members arrived on goes away while their batch is still pending elsewhere
        itemReceiver.Latch();
        await itemReceiver.DrainAsync();
        await itemReceiver.DisposeAsync();

        members.ShouldAllBe(x => InboxCustody.IsHeldForBatch(x));

        ((IMessageInboxHeartbeat)theInbox).ClearReceivedCalls();
        await waitForAsync(() => members.All(theClockWasRestartedFor),
            "Disposing the receiver stopped the heartbeat of batch members still waiting on their batch");

        // The batch finishes with them, and the heartbeat has nothing left to do
        await batchReceiver.CompleteAsync(new Envelope(new[] { "batch" }, members));

        itemReceiver.HeldBatchMemberCount.ShouldBe(0);
        await waitForAsync(() => !itemReceiver.HeartbeatIsRunning,
            "The heartbeat kept running after the last batch member settled");
    }
}
