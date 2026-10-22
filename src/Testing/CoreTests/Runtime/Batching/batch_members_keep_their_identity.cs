using JasperFx.Core;
using NSubstitute;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.Batching;

/// <summary>
/// The durable compliance findings F1, F2, F3 and B1/B2. Every member of a batch is a durable message in its own right,
/// with its own inbox row, and is settled as itself: when its batch reaches its terminal, or -- for a member taken out
/// of a failing batch by item isolation -- when its own replay does.
/// <para>
/// F1/B1: the replay built its members as <c>new Envelope(item)</c>, with no Destination and no row, while the
/// original members were marked Handled before the replay ran. Marking a replayed member then failed forever, a
/// probed poison member's dead letter write failed and the poison vanished, and a node stop lost the survivors.
/// F2: a dead letter write that failed inside item isolation escaped the continuation, and the pipeline's recovery
/// then completed the batch, marking every member Handled. F3/B2: a batch executing on a BUFFERED queue never settled
/// its members' inbox rows at all.
/// </para>
/// </summary>
public class batch_members_keep_their_identity : IAsyncLifetime
{
    private static readonly Uri theBatchQueue = new("local://identity-items");

    private readonly MockWolverineRuntime theRuntime = new();
    private readonly Exception theException = new DivideByZeroException();

    // AgentForLocalQueue is declared as returning ISendingAgent, and BatchReplay casts it to ILocalQueue
    private readonly ISendingAgent theQueueAgent = Substitute.For<ISendingAgent, ILocalQueue>();

    private DurableReceiver theDurableReceiver = null!;
    private BufferedReceiver theBufferedReceiver = null!;

    public ValueTask InitializeAsync()
    {
        theRuntime.Endpoints.AgentForLocalQueue(theBatchQueue).Returns(theQueueAgent);

        theDurableReceiver = new DurableReceiver(new StubEndpoint("durable-batches", new StubTransport()), theRuntime,
            Substitute.For<IHandlerPipeline>());
        theDurableReceiver.DeadLetterWritePauses = [10.Milliseconds()];

        theBufferedReceiver = new BufferedReceiver(new StubEndpoint("buffered-batches", new StubTransport()),
            theRuntime, Substitute.For<IHandlerPipeline>());
        theBufferedReceiver.BatchMembers.DeadLetterWritePauses = [10.Milliseconds()];

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        theDurableReceiver.SafeDispose();
        theBufferedReceiver.SafeDispose();
        await Task.CompletedTask;
    }

    /// <summary>Members received at a durable listener: each has its own Destination and inbox row</summary>
    private static Envelope[] durableMembers(params string[] names)
    {
        return names.Select(name => new Envelope(new IdentityItem(name))
        {
            Destination = new Uri("rabbitmq://queue/identity-items"),
            Status = EnvelopeStatus.Incoming,
            OwnerId = 1,
            WasPersistedInInbox = true
        }).ToArray();
    }

    private static Envelope carrierFor(Envelope[] members)
    {
        return new Envelope(members.Select(x => (IdentityItem)x.Message!).ToArray(), members)
        {
            Destination = theBatchQueue,
            MessageType = "identity-item-array"
        };
    }

    private MessageContext contextFor(Envelope carrier, IChannelCallback channel)
    {
        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(carrier, channel);
        return context;
    }

    private Envelope[] replayedBatches()
    {
        return ((ILocalQueue)theQueueAgent).ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(ILocalQueue.EnqueueAsync))
            .Select(x => (Envelope)x.GetArguments()[0]!)
            .ToArray();
    }

    /// <summary>Every envelope the inbox was asked to mark handled, through either overload</summary>
    private IReadOnlyList<Envelope> markedHandled()
    {
        var marked = new List<Envelope>();
        foreach (var call in theRuntime.Storage.Inbox.ReceivedCalls()
                     .Where(x => x.GetMethodInfo().Name == nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync)))
        {
            switch (call.GetArguments()[0])
            {
                case Envelope one:
                    marked.Add(one);
                    break;
                case IEnumerable<Envelope> many:
                    marked.AddRange(many);
                    break;
            }
        }

        return marked;
    }

    private void theDeadLetterWriteFailsFor(Envelope member)
    {
        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(Arg.Any<Envelope>(), Arg.Any<Exception?>())
            .Returns(call => ReferenceEquals(call.Arg<Envelope>(), member)
                ? Task.FromException(new InvalidOperationException("dead letter storage is refusing writes"))
                : Task.CompletedTask);
    }

    // ---------------------------------------------------------------------------------------------------------
    // F1 / B1

    [Fact]
    public async Task apply_item_replays_the_original_survivor_envelopes()
    {
        var members = durableMembers("a", "poison", "c");
        var carrier = carrierFor(members);

        await new ApplyItemContinuation(ApplyItemException.DeadLetterAndReplayOthers(members[1].Message!))
            .ExecuteAsync(contextFor(carrier, theDurableReceiver), theRuntime, DateTimeOffset.UtcNow, null);

        // The very same envelopes, with their Destination and their inbox rows. Rebuilt ones had neither, so
        // settling them failed forever and a poison among them could not be dead lettered
        var replayed = replayedBatches().ShouldHaveSingleItem();
        replayed.Batch!.ShouldBe([members[0], members[2]]);
    }

    [Fact]
    public async Task apply_item_survivors_are_not_settled_before_their_replay_finishes()
    {
        var members = durableMembers("a", "poison", "c");
        var carrier = carrierFor(members);

        await new ApplyItemContinuation(ApplyItemException.DeadLetterAndReplayOthers(members[1].Message!))
            .ExecuteAsync(contextFor(carrier, theDurableReceiver), theRuntime, DateTimeOffset.UtcNow, null);
        await theDurableReceiver.DrainAsync();

        // Marked Handled while the replay existed only in memory, a node stop lost them
        markedHandled().ShouldNotContain(members[0]);
        markedHandled().ShouldNotContain(members[2]);

        // The poison is dead lettered as itself
        await theRuntime.Storage.Inbox.Received().MoveToDeadLetterStorageAsync(members[1], Arg.Any<Exception?>());

        // ...and the replayed batch's own terminal is what settles the survivors
        await ((IChannelCallback)theDurableReceiver).CompleteAsync(replayedBatches().Single());
        await theDurableReceiver.DrainAsync();

        markedHandled().ShouldContain(members[0]);
        markedHandled().ShouldContain(members[2]);
    }

    [Fact]
    public async Task probing_replays_each_original_member_and_settles_none_with_the_probed_batch()
    {
        var members = durableMembers("a", "b", "c");
        var carrier = carrierFor(members);
        carrier.Attempts = 1;

        await new ProbeIndividuallyContinuation(theException)
            .ExecuteAsync(contextFor(carrier, theDurableReceiver), theRuntime, DateTimeOffset.UtcNow, null);
        await theDurableReceiver.DrainAsync();

        replayedBatches().Select(x => x.Batch!.ShouldHaveSingleItem()).ShouldBe(members);
        markedHandled().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_probed_poison_singleton_is_dead_lettered_under_its_own_identity()
    {
        var poison = durableMembers("poison");
        var singleton = carrierFor(poison);

        await new ProbeIndividuallyContinuation(theException)
            .ExecuteAsync(contextFor(singleton, theDurableReceiver), theRuntime, DateTimeOffset.UtcNow, null);

        await theRuntime.Storage.Inbox.Received().MoveToDeadLetterStorageAsync(poison[0], Arg.Any<Exception?>());
    }

    [Fact]
    public async Task a_reduced_batch_takes_over_the_pending_counts_of_its_members()
    {
        var members = durableMembers("a", "b");
        var carrier = carrierFor(members);
        carrier.BatchPipelineCounted = true;

        BatchReplay.Detach(carrier, [members[0]]);
        await BatchReplay.EnqueueReducedBatchAsync(theRuntime, carrier, [members[0]]);

        carrier.Batch.ShouldBe([members[1]]);

        // Counted once, by whoever assembled the original batch; the reduced batch releases what it took over
        replayedBatches().Single().BatchPipelineCounted.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------------------------------------
    // F2

    [Fact]
    public async Task apply_item_whose_poison_cannot_be_dead_lettered_still_replays_the_survivors_and_loses_nothing()
    {
        var members = durableMembers("a", "poison", "c");
        var carrier = carrierFor(members);
        theDeadLetterWriteFailsFor(members[1]);

        // Used to escape, so the pipeline's recovery completed the batch and marked every member Handled
        await new ApplyItemContinuation(ApplyItemException.DeadLetterAndReplayOthers(members[1].Message!))
            .ExecuteAsync(contextFor(carrier, theDurableReceiver), theRuntime, DateTimeOffset.UtcNow, null);
        await theDurableReceiver.DrainAsync();

        replayedBatches().ShouldHaveSingleItem().Batch!.ShouldBe([members[0], members[2]]);

        // Handed back to be executed again, and never marked Handled
        await theRuntime.Storage.Inbox.Received().ScheduleExecutionAsync(members[1]);
        members[1].Status.ShouldBe(EnvelopeStatus.Scheduled);
        markedHandled().ShouldNotContain(members[1]);
    }

    [Fact]
    public async Task a_probed_singleton_that_cannot_be_dead_lettered_is_handed_back_not_completed()
    {
        var poison = durableMembers("poison");
        var singleton = carrierFor(poison);
        theDeadLetterWriteFailsFor(poison[0]);

        await new ProbeIndividuallyContinuation(theException)
            .ExecuteAsync(contextFor(singleton, theDurableReceiver), theRuntime, DateTimeOffset.UtcNow, null);
        await theDurableReceiver.DrainAsync();

        await theRuntime.Storage.Inbox.Received().ScheduleExecutionAsync(poison[0]);
        markedHandled().ShouldNotContain(poison[0]);
        theRuntime.MessageTracking.Received().Requeued(singleton);
    }

    // ---------------------------------------------------------------------------------------------------------
    // F3 / B2: a batch executing on a buffered queue

    [Fact]
    public async Task a_batch_on_a_buffered_queue_settles_the_inbox_rows_of_its_durable_members()
    {
        var members = durableMembers("a", "b");
        var local = new Envelope(new IdentityItem("sent locally, no row"));
        var carrier = carrierFor([..members, local]);

        await ((IChannelCallback)theBufferedReceiver).CompleteAsync(carrier);
        await theBufferedReceiver.DrainAsync();

        // Left Incoming, every member ran again after the next restart
        markedHandled().ShouldBe(members, ignoreOrder: true);
    }

    [Fact]
    public async Task a_buffered_batch_member_that_cannot_be_dead_lettered_is_handed_back_and_the_rest_settle()
    {
        var members = durableMembers("a", "poison", "c");
        var carrier = carrierFor(members);
        theDeadLetterWriteFailsFor(members[1]);

        await new MoveToErrorQueue(theException)
            .ExecuteAsync(contextFor(carrier, theBufferedReceiver), theRuntime, DateTimeOffset.UtcNow, null);
        await theBufferedReceiver.DrainAsync();

        await theRuntime.Storage.Inbox.Received().ScheduleExecutionAsync(members[1]);
        members[1].Status.ShouldBe(EnvelopeStatus.Scheduled);
        members[1].OwnerId.ShouldBe(TransportConstants.AnyNode);

        markedHandled().ShouldNotContain(members[1]);
        markedHandled().ShouldContain(members[0]);
        markedHandled().ShouldContain(members[2]);
    }
}

public record IdentityItem(string Name);
