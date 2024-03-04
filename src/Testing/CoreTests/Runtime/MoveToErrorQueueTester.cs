using NSubstitute;
using TestingSupport;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.Runtime;

public class MoveToErrorQueueTester
{
    private readonly MoveToErrorQueue theContinuation;
    private readonly Envelope theEnvelope = ObjectMother.Envelope();

    private readonly Exception theException = new DivideByZeroException();
    private readonly IEnvelopeLifecycle theLifecycle = Substitute.For<IEnvelopeLifecycle>();
    private readonly MockWolverineRuntime theRuntime = new();

    public MoveToErrorQueueTester()
    {
        theContinuation = new MoveToErrorQueue(theException);
        theLifecycle.Envelope.Returns(theEnvelope);
    }

    [Fact]
    public async Task should_send_a_failure_ack()
    {
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle
                .Received()
                .SendFailureAcknowledgementAsync($"Moved message {theEnvelope.Id} to the Error Queue.\n{theException}")
            ;
    }

    [Fact]
    public async Task logging_calls()
    {
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        theRuntime.MessageTracking.Received().MessageFailed(theEnvelope, theException);
        theRuntime.MessageTracking.Received().MovedToErrorQueue(theEnvelope, theException);
    }
}