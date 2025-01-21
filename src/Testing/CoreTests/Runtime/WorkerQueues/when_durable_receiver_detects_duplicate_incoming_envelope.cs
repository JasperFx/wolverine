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

public class when_durable_receiver_detects_duplicate_incoming_envelope : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public when_durable_receiver_detects_duplicate_incoming_envelope()
    {
        theRuntime = new MockWolverineRuntime();


        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);

        theRuntime.Storage.Inbox.StoreIncomingAsync(theEnvelope)
            .Throws(new DuplicateIncomingEnvelopeException(theEnvelope));
    }

    public async Task InitializeAsync()
    {
        await theReceiver.ReceivedAsync(theListener, theEnvelope);

        // This will help prove that the envelope was NOT processed
        await theReceiver.DrainAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task the_listener_was_completed_to_remove_the_message()
    {
        await theListener.Received().CompleteAsync(theEnvelope);
    }

    [Fact]
    public async Task the_envelope_was_not_processed()
    {
        await thePipeline.DidNotReceive().InvokeAsync(theEnvelope, theReceiver);
    }
}