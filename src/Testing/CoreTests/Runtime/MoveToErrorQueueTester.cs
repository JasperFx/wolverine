using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Transports;
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
    public async Task never_sends_a_failure_ack_for_a_remote_destination()
    {
        // GH-5521: EnableAutomaticFailureAcks (off by default since 4.6) is gone in 7.0, and with it the
        // failure ack that moving a message to the error queue used to send
        theEnvelope.Destination = new Uri("tcp://localhost:9000");

        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle.Received().MoveToDeadLetterQueueAsync(theException);
        await theLifecycle.DidNotReceive().SendFailureAcknowledgementAsync(Arg.Any<string>());
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
    public async Task a_move_the_transport_could_not_make_leaves_the_envelope_unsettled()
    {
        // The transport left the delivery with the broker, which delivers it again. Completing it would settle,
        // and so lose, the only copy
        theLifecycle.MoveToDeadLetterQueueAsync(theException)
            .Returns(Task.FromException(new DeadLetterMoveFailedException("no dead letter stream", null)));

        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle.DidNotReceive().CompleteAsync();
        theRuntime.MessageTracking.DidNotReceive().MovedToErrorQueue(theEnvelope, theException);
        theRuntime.MessageTracking.Received().Requeued(theEnvelope);
    }

    [Fact]
    public async Task a_move_handed_back_for_redelivery_sends_no_failure_ack()
    {
        // The redelivery is a new envelope that comes back through here, so an ack now would be one per redelivery
        theEnvelope.Destination = new Uri("tcp://localhost:9000");
        theLifecycle.MoveToDeadLetterQueueAsync(theException)
            .Returns(Task.FromException(new DeadLetterMoveFailedException("no dead letter stream", null)));

        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle.DidNotReceive().SendFailureAcknowledgementAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task any_other_failure_of_the_move_is_rethrown()
    {
        theEnvelope.Destination = new Uri("tcp://localhost:9000");
        var moveFailure = new InvalidOperationException("the database is down");
        theLifecycle.MoveToDeadLetterQueueAsync(theException).Returns(Task.FromException(moveFailure));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null).AsTask());

        thrown.ShouldBeSameAs(moveFailure);
        await theLifecycle.DidNotReceive().SendFailureAcknowledgementAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task logging_calls()
    {
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        theRuntime.MessageTracking.Received().MovedToErrorQueue(theEnvelope, theException);

        // GH-4136: exactly ONE terminal tracking event on this path. A terminal record sweeps every
        // earlier record for the envelope complete, and the durable transports' dead-letter move emits
        // a trailing Sent that nothing else completes -- so a second terminal record here cannot be
        // ordered to satisfy both that sweep and the guarantee that the ending record is present.
        // MessageFailed's dead-letter counter, effective time and failure wire tap moved into
        // MovedToErrorQueue rather than being dropped.
        theRuntime.MessageTracking.DidNotReceive().MessageFailed(theEnvelope, theException);
    }

    [Fact]
    public async Task does_not_NRE_when_envelope_has_no_destination()
    {
        // GH-3013: a malformed system envelope (no Destination) must not NRE in the continuation
        theEnvelope.Destination = null;

        // A throw here fails the test
        await theContinuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        // No failure ack attempted, and the move-to-DLQ still happens.
        await theLifecycle.DidNotReceive().SendFailureAcknowledgementAsync(Arg.Any<string>());
        await theLifecycle.Received().MoveToDeadLetterQueueAsync(theException);
    }
}