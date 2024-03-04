using NSubstitute;
using TestingSupport;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class MessageSucceededContinuationTester
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IEnvelopeLifecycle theLifecycle = Substitute.For<IEnvelopeLifecycle>();

    private readonly MockWolverineRuntime theRuntime = new();

    public MessageSucceededContinuationTester()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.Message = new object();

        theLifecycle.Envelope.Returns(theEnvelope);

        MessageSucceededContinuation.Instance
            .ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);
    }

    [Fact]
    public void should_mark_the_message_as_successful()
    {
        theLifecycle.Received().CompleteAsync();
    }

    [Fact]
    public void should_send_off_all_queued_up_cascaded_messages()
    {
        theLifecycle.Received().FlushOutgoingMessagesAsync();
    }
}

public class MessageSucceededContinuation_failure_handling_Tester
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly Exception theException = new DivideByZeroException();
    private readonly IEnvelopeLifecycle theLifecycle = Substitute.For<IEnvelopeLifecycle>();
    private readonly MockWolverineRuntime theRuntime = new();

    public MessageSucceededContinuation_failure_handling_Tester()
    {
        theLifecycle.When(x => x.FlushOutgoingMessagesAsync())
            .Throw(theException);

        theLifecycle.Envelope.Returns(theEnvelope);

        MessageSucceededContinuation.Instance
            .ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);
    }

    [Fact]
    public void should_send_a_failure_ack()
    {
        var message = "Sending cascading message failed: " + theException.Message;
        theLifecycle.Received().SendFailureAcknowledgementAsync(message);
    }
}