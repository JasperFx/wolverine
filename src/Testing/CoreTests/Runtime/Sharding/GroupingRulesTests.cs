using Wolverine.ComplianceTests;
using Wolverine.Runtime.Sharding;
using Xunit;

namespace CoreTests.Runtime.Sharding;

public class GroupingRulesTests
{
    [Fact]
    public void by_tenant_id()
    {
        var rules = new GroupingRules();
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
        var rules = new GroupingRules();
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
        var rules = new GroupingRules();
        rules.ByMessage<Coffee1>(x => x.Brand);
        rules.ByMessage<ICoffee>(x => x.Name);
        rules.ByMessage<Latte>(x => x.NumberOfShots.ToString());

        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "Code Red";
        envelope.Message = new Coffee1("Dark", "Paul Newman's");
        
        rules.DetermineGroupId(envelope).ShouldBe("Code Red");
    }
}

public interface ICoffee
{
    string Name { get; }
}

public record Coffee1(string Name, string Brand) : ICoffee;
public record Coffee2(string Name) : ICoffee;
public record Coffee3(string Name) : ICoffee;

public record Latte(int NumberOfShots);