using Wolverine;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Acceptance;

public class GroupIdMessageBatcherTests
{
    private static GroupIdMessageBatcher<ScoreEvent> batcher(MessagePartitioningRules? rules = null)
    {
        var batcher = new GroupIdMessageBatcher<ScoreEvent>();
        if (rules != null) batcher.Rules = rules;
        return batcher;
    }

    private static Envelope envelopeFor(ScoreEvent message, string? groupId = null, string? tenantId = null)
    {
        return new Envelope(message) { GroupId = groupId, TenantId = tenantId };
    }

    [Fact]
    public void one_batch_per_group_id_and_the_batch_carries_that_group_id()
    {
        var envelopes = new[]
        {
            envelopeFor(new ScoreEvent("A", 1), "one"),
            envelopeFor(new ScoreEvent("A", 2), "two"),
            envelopeFor(new ScoreEvent("A", 3), "one")
        };

        var batches = batcher().Group(envelopes).ToArray();

        batches.Length.ShouldBe(2);

        // The stamp is the point: without it the batch has no group id, and an envelope with no
        // group id draws a RANDOM partition slot rather than being left unpartitioned.
        var one = batches.Single(x => x.GroupId == "one");
        one.Message.ShouldBeOfType<ScoreEvent[]>().Length.ShouldBe(2);
        one.Batch!.Length.ShouldBe(2);

        batches.Single(x => x.GroupId == "two").Message.ShouldBeOfType<ScoreEvent[]>().Length.ShouldBe(1);
    }

    [Fact]
    public void never_mixes_tenants_into_one_batch()
    {
        var envelopes = new[]
        {
            envelopeFor(new ScoreEvent("A", 1), "one", "tenant1"),
            envelopeFor(new ScoreEvent("A", 2), "one", "tenant2"),
            envelopeFor(new ScoreEvent("A", 3), "one", "tenant1")
        };

        var batches = batcher().Group(envelopes).ToArray();

        // Same group id, two tenants -> two batches. Members settle against the batch envelope, so
        // merging them would lose the tenant each member arrived under.
        batches.Length.ShouldBe(2);
        batches.ShouldAllBe(x => x.GroupId == "one");
        batches.Single(x => x.TenantId == "tenant1").Batch!.Length.ShouldBe(2);
        batches.Single(x => x.TenantId == "tenant2").Batch!.Length.ShouldBe(1);
    }

    [Fact]
    public void resolves_the_group_id_from_the_partitioning_rules_when_the_envelope_has_none()
    {
        var rules = new MessagePartitioningRules(new WolverineOptions());
        rules.ByMessage<ScoreEvent>(x => x.AggregateId);

        var envelopes = new[]
        {
            envelopeFor(new ScoreEvent("A", 1)),
            envelopeFor(new ScoreEvent("B", 1)),
            envelopeFor(new ScoreEvent("A", 2))
        };

        var batches = batcher(rules).Group(envelopes).ToArray();

        batches.Length.ShouldBe(2);
        batches.Single(x => x.GroupId == "A").Batch!.Length.ShouldBe(2);
        batches.Single(x => x.GroupId == "B").Batch!.Length.ShouldBe(1);
    }

    [Fact]
    public void ungroupable_envelopes_batch_together_and_stay_ungrouped()
    {
        // No rules and no GroupId — this batcher does not invent one, it just does not make things
        // worse than the default batcher does.
        var batches = batcher().Group([
            envelopeFor(new ScoreEvent("A", 1)),
            envelopeFor(new ScoreEvent("B", 1))
        ]).ToArray();

        var single = batches.ShouldHaveSingleItem();
        single.GroupId.ShouldBeNull();
        single.Batch!.Length.ShouldBe(2);
    }

    [Fact]
    public void batch_message_type_is_the_array_of_the_element_type()
    {
        batcher().BatchMessageType.ShouldBe(typeof(ScoreEvent[]));
    }
}
