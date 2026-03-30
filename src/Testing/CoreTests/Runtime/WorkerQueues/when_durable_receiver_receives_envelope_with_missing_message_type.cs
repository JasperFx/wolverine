using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class when_durable_receiver_receives_envelope_with_missing_message_type : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public when_durable_receiver_receives_envelope_with_missing_message_type()
    {
        theRuntime = new MockWolverineRuntime();

        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);

        theEnvelope.MessageType = null;
    }

    public async Task InitializeAsync()
    {
        await theReceiver.ReceivedAsync(theListener, theEnvelope);
        await theReceiver.DrainAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task the_listener_was_completed_so_the_transport_does_not_redeliver_the_message()
    {
        await theListener.Received().CompleteAsync(theEnvelope);
    }

    [Fact]
    public async Task the_envelope_was_not_processed()
    {
        await thePipeline.DidNotReceive().InvokeAsync(theEnvelope, theReceiver);
    }
}
