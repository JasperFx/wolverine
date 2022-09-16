using System;
using System.Threading.Tasks;
using CoreTests.Messaging;
using NSubstitute;
using Wolverine;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.Runtime;

public class MoveToErrorQueueTester
{
    private readonly IMessageContext theContext = Substitute.For<IMessageContext>();
    private readonly MoveToErrorQueue theContinuation;
    private readonly Envelope theEnvelope = ObjectMother.Envelope();

    private readonly Exception theException = new DivideByZeroException();
    private readonly MockWolverineRuntime theRuntime = new();

    public MoveToErrorQueueTester()
    {
        theContinuation = new MoveToErrorQueue(theException);
        theContext.Envelope.Returns(theEnvelope);
    }

    [Fact]
    public async Task should_send_a_failure_ack()
    {
        await theContinuation.ExecuteAsync(theContext, theRuntime, DateTimeOffset.Now);

        await theContext
                .Received()
                .SendFailureAcknowledgementAsync($"Moved message {theEnvelope.Id} to the Error Queue.\n{theException}")
            ;
    }

    [Fact]
    public async Task logging_calls()
    {
        await theContinuation.ExecuteAsync(theContext, theRuntime, DateTimeOffset.Now);

        theRuntime.MessageLogger.Received().MessageFailed(theEnvelope, theException);
        theRuntime.MessageLogger.Received().MovedToErrorQueue(theEnvelope, theException);
    }
}
