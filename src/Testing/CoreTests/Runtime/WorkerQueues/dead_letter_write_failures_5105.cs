using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5105. A durable receiver's dead-letter move only POSTED the envelope to a background <c>RetryBlock</c>,
/// and <see cref="MoveToErrorQueue"/> then completed the envelope and recorded <c>MovedToErrorQueue</c> without
/// waiting for the write. A write that kept failing -- a database outage, or a tenant database that could not be
/// resolved -- was discarded after 3 attempts with one line at Information. The inbox row had been marked handled,
/// no dead-letter row existed, and the log, the tracker and the dead-letter metric all said it had been
/// dead lettered.
///
/// <para>
/// The broker delivery was settled when the inbox row was written, so that row is the only copy. The write is now
/// awaited, a write that cannot be made is reported to the caller as <see cref="DeadLetterMoveFailedException"/>
/// -- which leaves the envelope uncompleted and unreported -- and the row is handed back to be executed again.
/// </para>
/// </summary>
public class dead_letter_write_failures_5105 : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly Exception theException = new DivideByZeroException();
    private readonly MockWolverineRuntime theRuntime = new();
    private DurableReceiver theReceiver = null!;

    private int _writes;

    public ValueTask InitializeAsync()
    {
        theReceiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), theRuntime,
            Substitute.For<IHandlerPipeline>());

        // Keep the give-up quick for the tests
        theReceiver.DeadLetterWritePauses = [10.Milliseconds(), 10.Milliseconds(), 10.Milliseconds()];

        theEnvelope.Status = EnvelopeStatus.Incoming;
        theEnvelope.OwnerId = theRuntime.DurabilitySettings.AssignedNodeNumber;
        theEnvelope.WasPersistedInInbox = true;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theReceiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The store refuses the first <paramref name="failures" /> dead-letter writes, then accepts.</summary>
    private void theDeadLetterWriteFailsFor(int failures)
    {
        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(Arg.Any<Envelope>(), Arg.Any<Exception?>()).Returns(_ =>
        {
            Interlocked.Increment(ref _writes);
            return _writes <= failures
                ? Task.FromException(new InvalidOperationException("the tenant database could not be resolved"))
                : Task.CompletedTask;
        });
    }

    [Fact]
    public async Task the_move_does_not_return_before_the_dead_letter_row_is_written()
    {
        var write = new TaskCompletionSource();
        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(theEnvelope, Arg.Any<Exception?>()).Returns(write.Task);

        var move = theReceiver.MoveToErrorsAsync(theEnvelope, theException);

        // Before GH-5105 the move had already "finished" here, with nothing written
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        move.IsCompleted.ShouldBeFalse();

        write.SetResult();
        await move.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_write_that_cannot_be_made_is_reported_as_a_failed_move()
    {
        theDeadLetterWriteFailsFor(int.MaxValue);

        await Should.ThrowAsync<DeadLetterMoveFailedException>(async () =>
            await theReceiver.MoveToErrorsAsync(theEnvelope, theException).WaitAsync(30.Seconds(), TestContext.Current.CancellationToken));

        _writes.ShouldBe(4);
    }

    [Fact]
    public async Task a_failed_move_hands_the_inbox_row_back_to_be_executed_again()
    {
        theDeadLetterWriteFailsFor(int.MaxValue);

        await Should.ThrowAsync<DeadLetterMoveFailedException>(async () =>
            await theReceiver.MoveToErrorsAsync(theEnvelope, theException).WaitAsync(30.Seconds(), TestContext.Current.CancellationToken));

        await theReceiver.DrainAsync();

        // Scheduled for any node, so the scheduled-job poller picks it up again rather than the row sitting
        // Incoming and owned by this live node, which nothing reclaims (GH-4664)
        await theRuntime.Storage.Inbox.Received().ScheduleExecutionAsync(theEnvelope);
        theEnvelope.Status.ShouldBe(EnvelopeStatus.Scheduled);
        theEnvelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public async Task a_write_that_recovers_inside_the_retry_budget_is_a_successful_move()
    {
        theDeadLetterWriteFailsFor(2);

        await theReceiver.MoveToErrorsAsync(theEnvelope, theException).WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        _writes.ShouldBe(3);
        await theRuntime.Storage.Inbox.DidNotReceive().ScheduleExecutionAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task move_to_error_queue_neither_completes_nor_reports_an_envelope_it_could_not_dead_letter()
    {
        theDeadLetterWriteFailsFor(int.MaxValue);

        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(theEnvelope, theReceiver);

        await new MoveToErrorQueue(theException).ExecuteAsync(context, theRuntime, DateTimeOffset.UtcNow, null)
            .AsTask().WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        await theReceiver.DrainAsync();

        // Completing would have marked the only copy handled
        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(theEnvelope);
        theRuntime.MessageTracking.DidNotReceive().MovedToErrorQueue(theEnvelope, Arg.Any<Exception>());
        theRuntime.MessageTracking.Received().Requeued(theEnvelope);
    }

    [Fact]
    public async Task move_to_error_queue_completes_and_reports_an_envelope_it_dead_lettered()
    {
        // The control
        theDeadLetterWriteFailsFor(0);

        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(theEnvelope, theReceiver);

        await new MoveToErrorQueue(theException).ExecuteAsync(context, theRuntime, DateTimeOffset.UtcNow, null);

        await theReceiver.DrainAsync();

        _writes.ShouldBe(1);
        theRuntime.MessageTracking.Received().MovedToErrorQueue(theEnvelope, theException);
    }

    [Fact]
    public async Task every_member_of_a_batch_is_attempted_even_when_one_cannot_be_dead_lettered()
    {
        var members = new[] { ObjectMother.Envelope(), ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var member in members)
        {
            member.Status = EnvelopeStatus.Incoming;
            member.WasPersistedInInbox = true;
        }

        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(Arg.Any<Envelope>(), Arg.Any<Exception?>())
            .Returns(call => call.Arg<Envelope>() == members[0]
                ? Task.FromException(new InvalidOperationException("the tenant database could not be resolved"))
                : Task.CompletedTask);

        theEnvelope.Batch = members;
        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(theEnvelope, theReceiver);

        await Should.ThrowAsync<DeadLetterMoveFailedException>(async () =>
            await context.MoveToDeadLetterQueueAsync(theException).WaitAsync(30.Seconds(), TestContext.Current.CancellationToken));

        // Stopping at the first failure left the later members Incoming and owned by this node
        await theRuntime.Storage.Inbox.Received().MoveToDeadLetterStorageAsync(members[1], Arg.Any<Exception?>());
        await theRuntime.Storage.Inbox.Received().MoveToDeadLetterStorageAsync(members[2], Arg.Any<Exception?>());
    }

    [Fact]
    public async Task an_envelope_dead_lettered_on_receipt_is_handed_back_to_the_broker_when_the_write_fails()
    {
        // Unwrapping failed, so the envelope never reached the inbox and the broker delivery is its only copy
        theDeadLetterWriteFailsFor(int.MaxValue);

        var serializer = Substitute.For<IUnwrapsMetadataMessageSerializer>();
        serializer.When(s => s.Unwrap(Arg.Any<Envelope>())).Throw(new Exception("Failed to unwrap metadata"));

        var envelope = ObjectMother.Envelope();
        envelope.MessageType = null;
        envelope.Serializer = serializer;

        var listener = Substitute.For<IListener>();

        await theReceiver.ReceivedAsync(listener, envelope);
        await theReceiver.DrainAsync();

        await listener.DidNotReceive().CompleteAsync(envelope);
        await listener.Received().DeferAsync(envelope);
    }
}
