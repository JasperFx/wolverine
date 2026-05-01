using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class when_durable_receiver_detects_duplicate_incoming_envelopes_in_batch : IAsyncLifetime
{
    private readonly Envelope theDuplicate = ObjectMother.Envelope();
    private readonly Envelope theFreshEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public when_durable_receiver_detects_duplicate_incoming_envelopes_in_batch()
    {
        theRuntime = new MockWolverineRuntime();
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);

        // The batch insert fails with a typed duplicate exception. DurableReceiver
        // re-posts every envelope through the per-envelope path, where the single
        // StoreIncomingAsync correctly distinguishes the actual duplicate from the
        // fresh one.
        theRuntime.Storage.Inbox
            .StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new DuplicateIncomingEnvelopeException(new[] { theDuplicate }));

        theRuntime.Storage.Inbox
            .StoreIncomingAsync(theDuplicate)
            .Throws(new DuplicateIncomingEnvelopeException(theDuplicate));

        theRuntime.Storage.Inbox
            .StoreIncomingAsync(theFreshEnvelope)
            .Returns(Task.CompletedTask);
    }

    public async Task InitializeAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await theReceiver.ProcessReceivedMessagesAsync(now, theListener, new[] { theDuplicate, theFreshEnvelope });
        await theReceiver.DrainAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task the_duplicate_listener_was_completed()
    {
        await theListener.Received().CompleteAsync(theDuplicate);
    }

    [Fact]
    public async Task the_duplicate_envelope_was_not_processed()
    {
        await thePipeline.DidNotReceive().InvokeAsync(theDuplicate, theReceiver);
    }

    [Fact]
    public async Task the_fresh_envelope_was_re_attempted_through_the_per_envelope_path()
    {
        // The per-envelope StoreIncomingAsync is the deduplication checkpoint:
        // a duplicate throws and is completed at the listener; a fresh envelope
        // succeeds and the receiver enqueues it for the pipeline. Asserting at
        // the inbox layer verifies the fall-back contract directly, without
        // depending on the receiver's downstream Dataflow drain ordering.
        await theRuntime.Storage.Inbox.Received().StoreIncomingAsync(theFreshEnvelope);
    }
}
