using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4851. A requeue from a durable external listener reported attempts 1 then 3: <c>Executor</c> increments
/// <c>Attempts</c> as every execution starts, and <c>DurableReceiver.DeferAsync</c> incremented it again for any
/// envelope not sent by a <c>DurableLocalQueue</c>. GH-826 had removed the second increment for local queues
/// only. The behavioural proof on a real database queue is
/// <c>PostgresqlTests.Transport.global_partition_slot_lifecycle_through_the_shard_queue.a_requeued_message_retries_on_the_owner_and_settles_its_row_for_both_ways_in</c>;
/// this pins the receiver's half: deferring never touches the count, and what it persists is the attempt that
/// just failed, so a recovered row resumes at the same number an in-memory requeue reaches.
/// </summary>
public class requeue_does_not_double_count_attempts_4851
{
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();

    [Fact]
    public async Task deferring_an_envelope_from_an_external_listener_leaves_attempts_to_the_executor()
    {
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>()).Returns(Task.CompletedTask);
        var receiver = new DurableReceiver(new StubEndpoint("4851", new StubTransport()), theRuntime, thePipeline);

        // Shaped like an envelope a database queue popped: no Sender, already in the inbox, one execution behind it
        var envelope = ObjectMother.Envelope();
        envelope.WasPersistedInInbox = true;
        envelope.Attempts = 1;

        await receiver.DeferAsync(envelope);

        envelope.Attempts.ShouldBe(1, "The next execution counts itself; a second increment here is the double count");

        await waitForAsync(() => theRuntime.Storage.Inbox.ReceivedCalls()
            .Any(x => x.GetMethodInfo().Name == nameof(Wolverine.Persistence.Durability.IMessageInbox.IncrementIncomingEnvelopeAttemptsAsync)));

        // Persisted as the attempt that just failed, not the one about to run
        await theRuntime.Storage.Inbox.Received()
            .IncrementIncomingEnvelopeAttemptsAsync(Arg.Is<Envelope>(x => x.Id == envelope.Id && x.Attempts == 1));
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25.Milliseconds());
        }

        throw new TimeoutException("The attempt count was never persisted");
    }
}
