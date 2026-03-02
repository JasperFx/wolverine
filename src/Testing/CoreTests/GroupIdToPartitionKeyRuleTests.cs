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
}
