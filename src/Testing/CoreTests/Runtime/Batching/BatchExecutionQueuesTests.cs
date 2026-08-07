using NSubstitute;
using Wolverine;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;
using Xunit;

namespace CoreTests.Runtime.Batching;

public class BatchExecutionQueuesTests
{
    private readonly MessagePartitioningRules theRules = new(new WolverineOptions());
    private readonly ILocalQueue theFallback = queueFor("local://fallback");
    private readonly ILocalQueue[] theSlots =
    [
        queueFor("local://slot1"),
        queueFor("local://slot2"),
        queueFor("local://slot3"),
        queueFor("local://slot4")
    ];

    private static ILocalQueue queueFor(string uri)
    {
        var queue = Substitute.For<ILocalQueue>();
        queue.Uri.Returns(new Uri(uri));
        return queue;
    }

    private PartitionedBatchExecutionQueues theQueues => new(theSlots, theRules, theFallback);

    private static Envelope batchFor(string? groupId) => new(new object()) { GroupId = groupId };

    [Fact]
    public void a_group_id_always_picks_the_same_slot()
    {
        var first = theQueues.SelectQueue(batchFor("aaa"));

        for (var i = 0; i < 25; i++)
        {
            theQueues.SelectQueue(batchFor("aaa")).ShouldBeSameAs(first);
        }
    }

    [Fact]
    public void picks_the_same_slot_the_unbatched_messages_for_that_group_would_land_on()
    {
        // This is the whole point of the fix: the batch has to agree with SlotForSending, which is
        // what GlobalPartitionedRoute and PartitionedMessageTopology.SelectSlot use to place the
        // unbatched messages for the same group id. A different hash here silently reintroduces
        // the race.
        foreach (var groupId in new[] { "one", "two", "three", "orders-4815162342" })
        {
            var expected = theSlots[batchFor(groupId).SlotForSending(theSlots.Length, theRules)];
            theQueues.SelectQueue(batchFor(groupId)).ShouldBeSameAs(expected);
        }
    }

    [Fact]
    public void spreads_distinct_group_ids_over_more_than_one_slot()
    {
        var chosen = Enumerable.Range(0, 50)
            .Select(i => theQueues.SelectQueue(batchFor($"group-{i}")))
            .Distinct()
            .ToArray();

        chosen.Length.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void an_ungrouped_batch_falls_back_rather_than_drawing_a_random_slot()
    {
        theQueues.SelectQueue(batchFor(null)).ShouldBeSameAs(theFallback);
        theQueues.SelectQueue(batchFor(string.Empty)).ShouldBeSameAs(theFallback);
    }

    [Fact]
    public void the_single_queue_variant_ignores_the_group_id()
    {
        var queues = new SingleBatchExecutionQueue(theFallback);

        queues.SelectQueue(batchFor("aaa")).ShouldBeSameAs(theFallback);
        queues.SelectQueue(batchFor(null)).ShouldBeSameAs(theFallback);
    }

    [Fact]
    public void must_have_at_least_one_slot()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PartitionedBatchExecutionQueues([], theRules, theFallback));
    }
}
