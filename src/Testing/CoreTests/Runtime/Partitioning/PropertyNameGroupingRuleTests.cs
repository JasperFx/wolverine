using Wolverine.ComplianceTests;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

public class PropertyNameGroupingRuleTests
{
    [Fact]
    public void match_string_property()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new StringIdMessage("abc-123");

        rules.DetermineGroupId(envelope).ShouldBe("abc-123");
    }

    [Fact]
    public void match_guid_property()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var id = Guid.NewGuid();
        var envelope = ObjectMother.Envelope();
        envelope.Message = new GuidIdMessage(id);

        rules.DetermineGroupId(envelope).ShouldBe(id.ToString());
    }

    [Fact]
    public void match_int_property()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new IntIdMessage(42);

        rules.DetermineGroupId(envelope).ShouldBe("42");
    }

    [Fact]
    public void match_long_property()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new LongIdMessage(9876543210L);

        rules.DetermineGroupId(envelope).ShouldBe("9876543210");
    }

    [Fact]
    public void null_property_value_returns_empty_string()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new StringIdMessage(null!);

        rules.DetermineGroupId(envelope).ShouldBe(string.Empty);
    }

    [Fact]
    public void no_matching_property_returns_null()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new NoIdMessage("hello");

        rules.DetermineGroupId(envelope).ShouldBeNull();
    }

    [Fact]
    public void first_matching_property_name_wins()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("StreamId", "Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new BothIdsMessage("stream-1", "id-2");

        rules.DetermineGroupId(envelope).ShouldBe("stream-1");
    }

    [Fact]
    public void falls_through_to_second_property_name()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("StreamId", "Id");

        var envelope = ObjectMother.Envelope();
        envelope.Message = new StringIdMessage("abc");

        // StringIdMessage only has "Id", not "StreamId"
        rules.DetermineGroupId(envelope).ShouldBe("abc");
    }

    [Fact]
    public void memoizes_across_multiple_messages_of_same_type()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope1 = ObjectMother.Envelope();
        envelope1.Message = new StringIdMessage("first");

        var envelope2 = ObjectMother.Envelope();
        envelope2.Message = new StringIdMessage("second");

        rules.DetermineGroupId(envelope1).ShouldBe("first");
        rules.DetermineGroupId(envelope2).ShouldBe("second");
    }

    [Fact]
    public void works_with_different_message_types()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var guid = Guid.NewGuid();

        var envelope1 = ObjectMother.Envelope();
        envelope1.Message = new StringIdMessage("abc");

        var envelope2 = ObjectMother.Envelope();
        envelope2.Message = new IntIdMessage(42);

        var envelope3 = ObjectMother.Envelope();
        envelope3.Message = new GuidIdMessage(guid);

        var envelope4 = ObjectMother.Envelope();
        envelope4.Message = new LongIdMessage(999L);

        rules.DetermineGroupId(envelope1).ShouldBe("abc");
        rules.DetermineGroupId(envelope2).ShouldBe("42");
        rules.DetermineGroupId(envelope3).ShouldBe(guid.ToString());
        rules.DetermineGroupId(envelope4).ShouldBe("999");
    }

    [Fact]
    public void explicit_group_id_takes_precedence()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByPropertyNamed("Id");

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "explicit";
        envelope.Message = new StringIdMessage("from-property");

        rules.DetermineGroupId(envelope).ShouldBe("explicit");
    }
}

/// <summary>
/// GH-4811. Under Native AOT the grouping accessor cannot be a closed <c>Grouper&lt;TConcrete,TProperty&gt;</c>
/// — the property type is routinely a value type, and no rooting mechanism reaches that instantiation — so
/// <see cref="Groupers.For" /> hands back a <see cref="ReflectiveGrouper" /> instead. The two have to agree
/// on every group id, or partitioning silently reshuffles when an application is published natively: the
/// same message would land in a different slot, which is exactly the guarantee partitioning exists to make.
/// Only the JIT path can be exercised in this suite, so parity is asserted against it directly.
/// </summary>
public class ReflectiveGrouperTests
{
    public static IEnumerable<object[]> Messages()
    {
        yield return [new StringIdMessage("abc-123")];
        yield return [new GuidIdMessage(Guid.NewGuid())];
        yield return [new IntIdMessage(42)];
        yield return [new LongIdMessage(9876543210L)];

        // The null case matters on its own: both sides have to answer "" rather than throw, because an
        // empty group id is what gets a message randomly sorted into the partition slots
        yield return [new StringIdMessage(null!)];
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void agrees_with_the_compiled_grouper(object message)
    {
        var messageType = message.GetType();
        var property = messageType.GetProperty("Id")!;

        var compiled = Groupers.For(messageType, property);
        var reflective = new ReflectiveGrouper(messageType, property);

        // Proof that the JIT path is the one being compared against, not a second reflective grouper
        compiled.GetType().GetGenericTypeDefinition().ShouldBe(typeof(Grouper<,>));

        reflective.ToGroupId(message).ShouldBe(compiled.ToGroupId(message));
        reflective.MessageType.ShouldBe(compiled.MessageType);
    }
}

public record StringIdMessage(string Id);
public record GuidIdMessage(Guid Id);
public record IntIdMessage(int Id);
public record LongIdMessage(long Id);
public record NoIdMessage(string Name);
public record BothIdsMessage(string StreamId, string Id);
