using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class MessageSucceededContinuationTester : IAsyncLifetime
{
    private Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IEnvelopeLifecycle theLifecycle = Substitute.For<IEnvelopeLifecycle>();

    private readonly MockWolverineRuntime theRuntime = new();

    public async Task InitializeAsync()
    {
        theEnvelope.Message = new object();

        theLifecycle.Envelope.Returns(theEnvelope);

        await MessageSucceededContinuation.Instance
            .ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task should_mark_the_message_as_successful()
    {
        await theLifecycle.Received().CompleteAsync();
    }

    [Fact]
    public async Task should_send_off_all_queued_up_cascaded_messages()
    {
        await theLifecycle.Received().FlushOutgoingMessagesAsync();
    }
}

public class MessageSucceededContinuation_failure_handling_Tester : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly Exception theException = new DivideByZeroException();
    private readonly IEnvelopeLifecycle theLifecycle = Substitute.For<IEnvelopeLifecycle>();
    private readonly MockWolverineRuntime theRuntime = new();

    public async Task InitializeAsync()
    {
        theLifecycle.When(x => x.FlushOutgoingMessagesAsync())
            .Throw(theException);

        theLifecycle.Envelope.Returns(theEnvelope);

        await MessageSucceededContinuation.Instance
            .ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task should_send_a_failure_ack()
    {
        var message = "Sending cascading message failed: " + theException.Message;
        await theLifecycle.Received().SendFailureAcknowledgementAsync(message);
    }
}