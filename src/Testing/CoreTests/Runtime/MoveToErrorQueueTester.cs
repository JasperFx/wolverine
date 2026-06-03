using NSubstitute;
using Wolverine.ComplianceTests;
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
    public async Task should_send_a_failure_ack_if_not_local()
    {
        theRuntime.Options.EnableAutomaticFailureAcks = true;
        
        theEnvelope.Destination = new Uri("tcp://localhost:9000");
        
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle
                .Received()
                .SendFailureAcknowledgementAsync($"Moved message {theEnvelope.Id} to the Error Queue.\n{theException}")
            ;
    }
    
    [Fact]
    public async Task should_not_send_a_failure_ack_if_wolverine_options_latches_that()
    {
        theEnvelope.Destination = new Uri("local://foo");
        theRuntime.Options.EnableAutomaticFailureAcks = false;
        
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle
                .DidNotReceive()
                .SendFailureAcknowledgementAsync($"Moved message {theEnvelope.Id} to the Error Queue.\n{theException}")
            ;
    }
    
    [Fact]
    public async Task should_not_send_a_failure_ack_if_local()
    {
        theEnvelope.Destination = new Uri("local://foo");
        
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle
                .DidNotReceive()
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

    [Fact]
    public async Task does_not_NRE_when_envelope_has_no_destination()
    {
        // GH-3013: a malformed system envelope (no Destination) must not NRE on the
        // `lifecycle.Envelope!.Destination!.Scheme` read before EnableAutomaticFailureAcks
        // even gets a chance to short-circuit. Enabling acks reproduces the pre-fix bug.
        theRuntime.Options.EnableAutomaticFailureAcks = true;
        theEnvelope.Destination = null;

        // A throw here fails the test — pre-fix this NRE'd at the `scheme` variable init.
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        // No scheme → no failure ack attempted, and the move-to-DLQ still happens.
        await theLifecycle.DidNotReceive().SendFailureAcknowledgementAsync(Arg.Any<string>());
        await theLifecycle.Received().MoveToDeadLetterQueueAsync(theException);
    }
}