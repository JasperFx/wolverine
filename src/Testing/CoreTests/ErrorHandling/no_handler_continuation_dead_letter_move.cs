using CoreTests.Runtime;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.ErrorHandling;

/// <summary>
/// With <c>UnknownMessageBehavior.DeadLetterQueue</c> a message without a handler is moved to the dead letter queue by
/// a missing handler. A transport that could not make the move leaves the delivery with the broker and says so with
/// <see cref="DeadLetterMoveFailedException"/>; completing the envelope afterwards settled, and so lost, the only copy.
/// </summary>
public class no_handler_continuation_dead_letter_move
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly IEnvelopeLifecycle theLifecycle = Substitute.For<IEnvelopeLifecycle>();
    private readonly MockWolverineRuntime theRuntime = new();

    public no_handler_continuation_dead_letter_move()
    {
        theLifecycle.Envelope.Returns(theEnvelope);
    }

    [Fact]
    public async Task a_move_the_transport_could_not_make_leaves_the_envelope_unsettled()
    {
        theLifecycle.MoveToDeadLetterQueueAsync(Arg.Any<Exception>())
            .Returns(Task.FromException(new DeadLetterMoveFailedException("no dead letter stream", null)));

        var continuation = new NoHandlerContinuation([new MoveUnknownMessageToDeadLetterQueue()], theRuntime);
        await continuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle.DidNotReceive().CompleteAsync();
        theRuntime.MessageTracking.Received().Requeued(theEnvelope);
    }

    [Fact]
    public async Task a_moved_envelope_is_still_completed()
    {
        var continuation = new NoHandlerContinuation([new MoveUnknownMessageToDeadLetterQueue()], theRuntime);
        await continuation.ExecuteAsync(theLifecycle, theRuntime, DateTimeOffset.Now, null);

        await theLifecycle.Received().MoveToDeadLetterQueueAsync(Arg.Any<Exception>());
        await theLifecycle.Received().CompleteAsync();
    }
}
