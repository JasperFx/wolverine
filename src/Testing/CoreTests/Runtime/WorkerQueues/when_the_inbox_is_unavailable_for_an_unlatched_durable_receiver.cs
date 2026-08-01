using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

// GH-3767. When the inbox database is unavailable and the receiver has not
// latched yet, an incoming broker delivery whose persistence fails must be
// settled with the broker (deferred) rather than discarded by RetryBlock
// exhaustion. A discarded delivery sits unacknowledged on a live consumer,
// where the broker never redelivers it -- real message loss under a durable
// inbox.
public class when_the_inbox_is_unavailable_for_an_unlatched_durable_receiver : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public when_the_inbox_is_unavailable_for_an_unlatched_durable_receiver()
    {
        theRuntime = new MockWolverineRuntime();
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);

        theRuntime.Storage.Inbox
            .StoreIncomingAsync(Arg.Any<Envelope>())
            .Throws(new InvalidOperationException("inbox database is down"));

        theRuntime.Storage.Inbox
            .StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new InvalidOperationException("inbox database is down"));
    }

    // No DrainAsync here on purpose: draining latches the receiver, and the
    // latched path has its own defer semantics. The bug lives in the window
    // before anything latches, so the settle must happen inline with the
    // failed receive itself.
    public async ValueTask InitializeAsync()
    {
        await theReceiver.ReceivedAsync(theListener, theEnvelope);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task the_delivery_was_settled_by_deferring_back_to_the_listener()
    {
        await theListener.Received().DeferAsync(theEnvelope);
    }

    [Fact]
    public async Task the_delivery_was_not_completed_at_the_listener()
    {
        await theListener.DidNotReceive().CompleteAsync(theEnvelope);
    }

    [Fact]
    public async Task the_envelope_was_not_processed()
    {
        await thePipeline.DidNotReceive().InvokeAsync(theEnvelope, theReceiver);
    }
}

// Same failure mode, but entering through the batch path: the batch insert
// fails, every envelope falls back to the per-envelope path, and each one must
// end up settled with the broker instead of discarded.
public class when_the_inbox_is_unavailable_for_an_unlatched_durable_receiver_receiving_a_batch : IAsyncLifetime
{
    private readonly Envelope[] theEnvelopes =
        [ObjectMother.Envelope(), ObjectMother.Envelope(), ObjectMother.Envelope()];

    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public when_the_inbox_is_unavailable_for_an_unlatched_durable_receiver_receiving_a_batch()
    {
        theRuntime = new MockWolverineRuntime();
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);

        theRuntime.Storage.Inbox
            .StoreIncomingAsync(Arg.Any<Envelope>())
            .Throws(new InvalidOperationException("inbox database is down"));

        theRuntime.Storage.Inbox
            .StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new InvalidOperationException("inbox database is down"));
    }

    // No DrainAsync here on purpose -- see the note on the single-envelope test above.
    public async ValueTask InitializeAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await theReceiver.ProcessReceivedMessagesAsync(now, theListener, theEnvelopes);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task every_delivery_was_settled_by_deferring_back_to_the_listener()
    {
        foreach (var envelope in theEnvelopes)
        {
            await theListener.Received().DeferAsync(envelope);
        }
    }

    [Fact]
    public async Task no_envelope_was_processed()
    {
        foreach (var envelope in theEnvelopes)
        {
            await thePipeline.DidNotReceive().InvokeAsync(envelope, theReceiver);
        }
    }
}
