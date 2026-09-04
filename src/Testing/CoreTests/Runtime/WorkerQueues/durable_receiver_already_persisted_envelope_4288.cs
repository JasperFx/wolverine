using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4288. A durable database-backed queue (sharded SQL Server / PostgreSQL slots in a global
/// partitioned topology) moves each envelope into the inbox as part of the dequeue, then the
/// GlobalPartitionedReceiverBridge forwards it to a companion local queue whose DurableReceiver is
/// NOT database-backed. Before the fix that receiver stored the envelope a second time, threw
/// DuplicateIncomingEnvelopeException on every message, and parked all of them in the inbox
/// permanently. An envelope arriving with WasPersistedInInbox = true must skip the inbox store and
/// still be settled and processed.
/// </summary>
public class durable_receiver_already_persisted_envelope_4288
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime = new();

    public durable_receiver_already_persisted_envelope_4288()
    {
        // A local queue style endpoint, so ShouldPersistBeforeProcessing is true
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);

        theEnvelope.WasPersistedInInbox = true;
    }

    [Fact]
    public async Task does_not_store_the_envelope_in_the_inbox_a_second_time()
    {
        await theReceiver.ReceivedAsync(theListener, theEnvelope);

        await theRuntime.Storage.Inbox.DidNotReceive().StoreIncomingAsync(Arg.Any<Envelope>());
        await theRuntime.Storage.Inbox.DidNotReceive().StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>());
    }

    [Fact]
    public async Task still_settles_the_delivery_with_the_listener()
    {
        await theReceiver.ReceivedAsync(theListener, theEnvelope);

        await theListener.Received().CompleteAsync(theEnvelope);
    }
}
