using NSubstitute;
using Wolverine;
using Wolverine.ComplianceTests;
using Xunit;

namespace CoreTests;

public class GroupIdToPartitionKeyRuleTests
{
    [Fact]
    public void propagates_group_id_to_partition_key()
    {
        var rule = new GroupIdToPartitionKeyRule();
        var originator = Substitute.For<IMessageContext>();
        var originatingEnvelope = ObjectMother.Envelope();
        originatingEnvelope.GroupId = "stream-123";
        originator.Envelope.Returns(originatingEnvelope);

        var outgoing = ObjectMother.Envelope();
        outgoing.PartitionKey = null;

        rule.ApplyCorrelation(originator, outgoing);

        outgoing.PartitionKey.ShouldBe("stream-123");
    }

    [Fact]
    public void does_not_override_existing_partition_key()
    {
        var rule = new GroupIdToPartitionKeyRule();
        var originator = Substitute.For<IMessageContext>();
        var originatingEnvelope = ObjectMother.Envelope();
        originatingEnvelope.GroupId = "stream-123";
        originator.Envelope.Returns(originatingEnvelope);

        var outgoing = ObjectMother.Envelope();
        outgoing.PartitionKey = "explicit-key";

        rule.ApplyCorrelation(originator, outgoing);

        outgoing.PartitionKey.ShouldBe("explicit-key");
    }

    [Fact]
    public void does_nothing_when_originator_has_no_group_id()
    {
        var rule = new GroupIdToPartitionKeyRule();
        var originator = Substitute.For<IMessageContext>();
        var originatingEnvelope = ObjectMother.Envelope();
        originatingEnvelope.GroupId = null;
        originator.Envelope.Returns(originatingEnvelope);

        var outgoing = ObjectMother.Envelope();
        outgoing.PartitionKey = null;

        rule.ApplyCorrelation(originator, outgoing);

        outgoing.PartitionKey.ShouldBeNull();
    }

    [Fact]
    public void does_nothing_when_originator_has_no_envelope()
    {
        var rule = new GroupIdToPartitionKeyRule();
        var originator = Substitute.For<IMessageContext>();
        originator.Envelope.Returns((Envelope?)null);

        var outgoing = ObjectMother.Envelope();
        outgoing.PartitionKey = null;

        rule.ApplyCorrelation(originator, outgoing);

        outgoing.PartitionKey.ShouldBeNull();
    }

    [Fact]
    public void prefers_outgoing_group_id_over_originator_group_id()
    {
        // Simulates: ByPropertyNamed has set outgoing.GroupId from the message property,
        // while the originator (a Kafka consumer) has GroupId = consumer group name.
        var rule = new GroupIdToPartitionKeyRule();
        var originator = Substitute.For<IMessageContext>();
        var originatingEnvelope = ObjectMother.Envelope();
        originatingEnvelope.GroupId = "my-application-name"; // Kafka consumer group
        originator.Envelope.Returns(originatingEnvelope);

        var outgoing = ObjectMother.Envelope();
        outgoing.PartitionKey = null;
        outgoing.GroupId = "fixture-abc"; // set by ByPropertyNamed

        rule.ApplyCorrelation(originator, outgoing);

        outgoing.PartitionKey.ShouldBe("fixture-abc");
    }

    [Fact]
    public void falls_back_to_originator_partition_key_when_outgoing_has_no_group_id()
    {
        // Simulates: no ByPropertyNamed, but originator has a PartitionKey (e.g. from Kafka message key)
        var rule = new GroupIdToPartitionKeyRule();
        var originator = Substitute.For<IMessageContext>();
        var originatingEnvelope = ObjectMother.Envelope();
        originatingEnvelope.PartitionKey = "fixture-xyz";
        originatingEnvelope.GroupId = null;
        originator.Envelope.Returns(originatingEnvelope);

        var outgoing = ObjectMother.Envelope();
        outgoing.PartitionKey = null;
        outgoing.GroupId = null;

        rule.ApplyCorrelation(originator, outgoing);

        outgoing.PartitionKey.ShouldBe("fixture-xyz");
    }

    [Fact]
    public void modify_promotes_group_id_to_partition_key_when_no_originator()
    {
        // Simulates: published outside a handler context (background service etc.)
        // ByPropertyNamed has set GroupId; Modify should promote it to PartitionKey.
        var rule = new GroupIdToPartitionKeyRule();
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "fixture-abc";
        envelope.PartitionKey = null;

        rule.Modify(envelope);

        envelope.PartitionKey.ShouldBe("fixture-abc");
    }

    [Fact]
    public void modify_does_not_override_explicit_partition_key()
    {
        var rule = new GroupIdToPartitionKeyRule();
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "fixture-abc";
        envelope.PartitionKey = "explicit-key";

        rule.Modify(envelope);

        envelope.PartitionKey.ShouldBe("explicit-key");
    }
}
