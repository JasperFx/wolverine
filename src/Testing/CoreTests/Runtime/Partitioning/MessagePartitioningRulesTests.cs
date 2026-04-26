using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

public class MessagePartitioningRulesTests
{
    [Fact]
    public void by_tenant_id()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByTenantId();
        
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = null;
        
        rules.DetermineGroupId(envelope).ShouldBeNull();
        
        envelope.TenantId = "red";
        
        rules.DetermineGroupId(envelope).ShouldBe("red");
    }

    [Fact]
    public void by_message()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage<Coffee1>(x => x.Brand);
        rules.ByMessage<ICoffee>(x => x.Name);
        rules.ByMessage<Latte>(x => x.NumberOfShots.ToString());

        var envelope = ObjectMother.Envelope();
        envelope.Message = new Coffee1("Dark", "Paul Newman's");
        rules.DetermineGroupId(envelope).ShouldBe("Paul Newman's");

        envelope = ObjectMother.Envelope();
        envelope.Message = new Coffee3("Starbucks");
        rules.DetermineGroupId(envelope).ShouldBe("Starbucks");

        envelope = ObjectMother.Envelope();
        envelope.Message = new Latte(3);
        rules.DetermineGroupId(envelope).ShouldBe("3");
    }

    [Fact]
    public void does_not_override_the_explicit_group_id()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage<Coffee1>(x => x.Brand);
        rules.ByMessage<ICoffee>(x => x.Name);
        rules.ByMessage<Latte>(x => x.NumberOfShots.ToString());

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "Code Red";
        envelope.Message = new Coffee1("Dark", "Paul Newman's");
        
        rules.DetermineGroupId(envelope).ShouldBe("Code Red");
    }

    [Fact]
    public void by_specific_messages_and_properties()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage(typeof(Coffee1), ReflectionHelper.GetProperty<Coffee1>(x => x.Name));
        rules.ByMessage(typeof(Coffee2), ReflectionHelper.GetProperty<Coffee2>(x => x.Name));
        rules.ByMessage(typeof(Coffee3), ReflectionHelper.GetProperty<Coffee3>(x => x.Name));
        
        var envelope = ObjectMother.Envelope();
        envelope.Message = new Coffee1("Dark", "Paul Newman's");
        rules.DetermineGroupId(envelope).ShouldBe("Dark");

        envelope = ObjectMother.Envelope();
        envelope.Message = new Coffee3("Starbucks");
        rules.DetermineGroupId(envelope).ShouldBe("Starbucks");
    }

    [Fact]
    public void by_message_type_and_property_respects_prior_rule_order()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByTenantId();
        rules.ByMessage(typeof(Coffee1), ReflectionHelper.GetProperty<Coffee1>(x => x.Name));

        var envelope = ObjectMother.Envelope();
        envelope.TenantId = "red";
        envelope.Message = new Coffee1("Dark", "Paul Newman's");

        rules.DetermineGroupId(envelope).ShouldBe("red");
    }

    [Fact]
    public void by_message_type_and_property_can_win_when_declared_first()
    {
        var rules = new MessagePartitioningRules(new());
        rules.ByMessage(typeof(Coffee1), ReflectionHelper.GetProperty<Coffee1>(x => x.Name));
        rules.ByTenantId();

        var envelope = ObjectMother.Envelope();
        envelope.TenantId = "red";
        envelope.Message = new Coffee1("Dark", "Paul Newman's");

        rules.DetermineGroupId(envelope).ShouldBe("Dark");
    }

    [Fact]
    public void sequenced_message_with_order_uses_order_as_group_id()
    {
        var rule = new SequencedMessageGroupingRule(typeof(TestSequencedMsg));

        var envelope = ObjectMother.Envelope();
        envelope.Message = new TestSequencedMsg(5);

        rule.TryFindIdentity(envelope, out var groupId).ShouldBeTrue();
        groupId.ShouldBe("5");
    }

    [Fact]
    public void sequenced_message_with_null_order_gets_random_group_id()
    {
        var rule = new SequencedMessageGroupingRule(typeof(TestSequencedMsg));

        var envelope = ObjectMother.Envelope();
        envelope.Message = new TestSequencedMsg(null);

        rule.TryFindIdentity(envelope, out var groupId).ShouldBeTrue();
        groupId.ShouldNotBeNullOrEmpty();

        // Should get a different random id each time
        rule.TryFindIdentity(envelope, out var groupId2).ShouldBeTrue();
        groupId2.ShouldNotBe(groupId);
    }

    [Fact]
    public void sequenced_message_rule_does_not_match_wrong_type()
    {
        var rule = new SequencedMessageGroupingRule(typeof(TestSequencedMsg));

        var envelope = ObjectMother.Envelope();
        envelope.Message = new Coffee1("Dark", "Paul Newman's");

        rule.TryFindIdentity(envelope, out _).ShouldBeFalse();
    }
}

public record TestSequencedMsg(int? Order) : SequencedMessage;

public interface ICoffee
{
    string Name { get; }
}

public record Coffee1(string Name, string Brand) : ICoffee;
public record Coffee2(string Name) : ICoffee;
public record Coffee3(string Name) : ICoffee;

public record Latte(int NumberOfShots);
