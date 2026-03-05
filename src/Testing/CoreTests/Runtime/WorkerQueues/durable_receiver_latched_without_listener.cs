using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class durable_receiver_latched_without_listener : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly DurableReceiver theReceiver;
    private readonly MockWolverineRuntime theRuntime;

    public durable_receiver_latched_without_listener()
    {
        theRuntime = new MockWolverineRuntime();

        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);
    }

    public async Task InitializeAsync()
    {
        // Latch the receiver to simulate draining/paused state
        theReceiver.Latch();

        // ReceivedAsync with a listener, but envelope.Listener is not yet set
        // because MarkReceived runs after the latched check. This previously
        // caused a NullReferenceException in the _deferBlock.
        await theReceiver.ReceivedAsync(theListener, theEnvelope);

        await theReceiver.DrainAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task should_not_throw_nre_and_should_not_invoke_pipeline()
    {
        await thePipeline.DidNotReceive().InvokeAsync(theEnvelope, theReceiver);
    }

    [Fact]
    public async Task should_defer_on_listener_when_envelope_has_listener()
    {
        // When an envelope DOES have a listener set before ReceivedAsync,
        // it should still defer properly
        var envelope2 = ObjectMother.Envelope();
        envelope2.Listener = theListener;

        var stubEndpoint = new StubEndpoint("two", new StubTransport());
        var receiver2 = new DurableReceiver(stubEndpoint, theRuntime, thePipeline);
        receiver2.Latch();

        await receiver2.ReceivedAsync(theListener, envelope2);
        await receiver2.DrainAsync();

        await theListener.Received().DeferAsync(envelope2);
    }
}
