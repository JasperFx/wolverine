using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// GH-4481. <c>DeadLetterMessageAsync</c> is a settle disposition: it consumes the delivery's lock exactly
/// as <c>CompleteMessageAsync</c> does. But <c>MoveToErrorQueue.ExecuteAsync</c> -- and
/// <c>NoHandlerContinuation</c> on the unknown-message-type path -- call <c>lifecycle.CompleteAsync()</c>
/// unconditionally right after the dead letter move, because for a transport whose
/// <c>MoveToErrorsAsync</c> only sends a COPY (SQS, GCP Pub/Sub) that trailing call is the only thing that
/// ever settles the original.
///
/// <para><c>Envelope.HasBeenAcked</c> is how a transport that already settled opts out of that trailing
/// call -- <c>MessageContext.CompleteAsync</c> short-circuits on it, which is exactly what RabbitMQ's two
/// dead letter paths rely on. Azure Service Bus was the one settling transport that never set it, so its
/// trailing complete always went to the broker on a lock its own dead letter move had already consumed.
/// On a non-session entity that returns "The lock supplied is invalid", which <c>CompleteAsync</c> swallows
/// -- harmless and invisible, which is why this survived. On a SESSION entity it returns
/// <c>SessionLockLost</c>, forcing the AMQP management link closed and reopened before the next session can
/// be accepted.</para>
/// </summary>
public class dead_letter_settles_the_delivery_4481
{
    private static AzureServiceBusEnvelope envelopeFor(ServiceBusReceiver receiver)
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(lockTokenGuid: Guid.NewGuid());

        return new AzureServiceBusEnvelope(message, receiver);
    }

    [Fact]
    public async Task a_successful_dead_letter_move_marks_the_envelope_settled()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        var envelope = envelopeFor(receiver);

        await envelope.DeadLetterAsync(CancellationToken.None, "SomeException", "it blew up");

        // HasBeenAcked is the core-facing half: it is what makes MessageContext.CompleteAsync a no-op, and
        // so what actually fixes the stray settle.
        envelope.HasBeenAcked.ShouldBeTrue(
            "Without this the trailing CompleteAsync() reaches the broker on a lock this move already consumed.");

        // IsCompleted is the listener-facing half, consistent with what GH-4068 put in CompleteAsync: it is
        // what the listeners' _defer guards and BatchedAzureServiceBusListener.RenewLeasesAsync read, so a
        // dead lettered delivery stops having its lock renewed.
        envelope.IsCompleted.ShouldBeTrue();
    }

    /// <summary>
    /// The marking has to be conditional on the move actually succeeding. A dead letter move that threw
    /// settled nothing, and the delivery must stay eligible for the lock lapse and redelivery that the
    /// settle blocks' terminal give-up (GH-4012 item 5) relies on for recovery.
    /// </summary>
    [Fact]
    public async Task a_failed_dead_letter_move_leaves_the_envelope_unsettled()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        receiver.DeadLetterMessageAsync(Arg.Any<ServiceBusReceivedMessage>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new ServiceBusException("lock is gone", ServiceBusFailureReason.MessageLockLost));

        var envelope = envelopeFor(receiver);

        await Should.ThrowAsync<ServiceBusException>(() =>
            envelope.DeadLetterAsync(CancellationToken.None, "SomeException", "it blew up"));

        envelope.HasBeenAcked.ShouldBeFalse();
        envelope.IsCompleted.ShouldBeFalse();
    }

    /// <summary>
    /// An envelope carrying no receiver of any kind settles nothing, so it must not claim to have. This is
    /// the branch <c>DeadLetterAsync</c> used to answer with <c>Task.CompletedTask</c>; marking it would
    /// suppress a trailing complete that never actually happened.
    /// </summary>
    [Fact]
    public async Task an_envelope_with_no_receiver_is_not_marked_settled()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(lockTokenGuid: Guid.NewGuid());
        var envelope = new AzureServiceBusEnvelope(message, (ServiceBusReceiver)null!);

        await envelope.DeadLetterAsync(CancellationToken.None);

        envelope.HasBeenAcked.ShouldBeFalse();
        envelope.IsCompleted.ShouldBeFalse();
    }

    /// <summary>
    /// GH-3474 carries the failure diagnostics onto the dead lettered message's application properties, and
    /// <c>buildDiagnosticProperties</c> sources them from the envelope's Headers -- so they have to have
    /// been stamped BEFORE the move. Three of the four listeners called
    /// <c>DeadLetterQueueConstants.StampFailureMetadata</c> in their <c>MoveToErrorsAsync</c>;
    /// <c>SessionSpecificListener</c> never did, so a message dead lettered from a session-specific
    /// listener reached $DeadLetterQueue with no diagnostics at all. This pins the mechanism that omission
    /// silently defeated.
    /// </summary>
    [Fact]
    public async Task stamped_failure_metadata_is_carried_onto_the_dead_lettered_message()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        var envelope = envelopeFor(receiver);

        DeadLetterQueueConstants.StampFailureMetadata(envelope, new InvalidOperationException("it blew up"));

        await envelope.DeadLetterAsync(CancellationToken.None, "InvalidOperationException", "it blew up");

        var properties = (Dictionary<string, object>?)receiver.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.DeadLetterMessageAsync))
            .GetArguments()[1];

        properties.ShouldNotBeNull("Without the stamp this is null and the diagnostics never reach the DLQ.");
        properties[DeadLetterQueueConstants.ExceptionTypeHeader]
            .ShouldBe(typeof(InvalidOperationException).FullName);
        properties[DeadLetterQueueConstants.ExceptionMessageHeader].ShouldBe("it blew up");
    }

    /// <summary>
    /// The test above pins the mechanism; this one pins the listener that was not using it.
    /// <c>SessionSpecificListener.MoveToErrorsAsync</c> is the one of the four that never stamped, so its
    /// dead lettered messages arrived with no diagnostics -- invisible, because the move itself succeeds
    /// either way and nothing asserts on what the DLQ copy carries.
    /// </summary>
    [Fact]
    public async Task the_session_specific_listener_stamps_before_dead_lettering()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.Options.RequiresSession = true;

        // Only the dead letter path is exercised, and that reads the receiver off the ENVELOPE, so the
        // listener's own session receiver is never touched here.
        var listener = new SessionSpecificListener(null!, queue, Substitute.For<IReceiver>(),
            Substitute.For<IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>>(),
            NullLogger.Instance, Substitute.For<ISender>());

        var receiver = Substitute.For<ServiceBusReceiver>();
        var envelope = envelopeFor(receiver);

        await listener.MoveToErrorsAsync(envelope, new InvalidOperationException("it blew up"));

        // Unstamped, buildDiagnosticProperties finds nothing and GH-3474's diagnostics are lost.
        envelope.Headers.TryGetValue(DeadLetterQueueConstants.ExceptionTypeHeader, out var exceptionType)
            .ShouldBeTrue();
        exceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
    }
}
