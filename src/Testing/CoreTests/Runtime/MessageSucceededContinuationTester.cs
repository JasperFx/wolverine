using System;
using CoreTests.Messaging;
using NSubstitute;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class MessageSucceededContinuationTester
{
    private readonly IMessageContext theContext = Substitute.For<IMessageContext>();
    private readonly Envelope theEnvelope = ObjectMother.Envelope();

    private readonly MockWolverineRuntime theRuntime = new();

    public MessageSucceededContinuationTester()
    {
        theEnvelope = ObjectMother.Envelope();
        theEnvelope.Message = new object();

        theContext.Envelope.Returns(theEnvelope);

        MessageSucceededContinuation.Instance
            .ExecuteAsync(theContext, theRuntime, DateTimeOffset.Now);
    }

    [Fact]
    public void should_mark_the_message_as_successful()
    {
        theContext.Received().CompleteAsync();
    }

    [Fact]
    public void should_send_off_all_queued_up_cascaded_messages()
    {
        theContext.Received().FlushOutgoingMessagesAsync();
    }
}

public class MessageSucceededContinuation_failure_handling_Tester
{
    private readonly IMessageContext theContext = Substitute.For<IMessageContext>();

    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly Exception theException = new DivideByZeroException();
    private readonly MockWolverineRuntime theRuntime = new();

    public MessageSucceededContinuation_failure_handling_Tester()
    {
        theContext.When(x => x.FlushOutgoingMessagesAsync())
            .Throw(theException);

        theContext.Envelope.Returns(theEnvelope);

        MessageSucceededContinuation.Instance
            .ExecuteAsync(theContext, theRuntime, DateTimeOffset.Now);
    }

    [Fact]
    public void should_send_a_failure_ack()
    {
        var message = "Sending cascading message failed: " + theException.Message;
        theContext.Received().SendFailureAcknowledgementAsync(message);
    }
}
