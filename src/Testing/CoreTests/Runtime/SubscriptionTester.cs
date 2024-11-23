using CoreTests.Runtime.Green;
using CoreTests.Runtime.Red;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

public class SubscriptionTester
{
    [Fact]
    public void description_of_assembly_rule()
    {
        var rule = new Subscription(typeof(RandomClass).Assembly);
        rule.ToString().ShouldBe("Message assembly is CoreTests");
    }

    [Fact]
    public void description_of_namespace_rule()
    {
        var rule = new Subscription
        {
            Match = typeof(RandomClass).Namespace,
            Scope = RoutingScope.Namespace
        };
        rule.ToString().ShouldBe("Message type is within namespace CoreTests.Runtime");
    }

    [Fact]
    public void description_of_type_rule()
    {
        var rule = Subscription.ForType(typeof(RandomClass));
        rule.ToString().ShouldBe("Message type is CoreTests.Runtime.RandomClass");
    }

    [Fact]
    public void description_of_type_name_rule()
    {
        var rule = new Subscription
        {
            Match = typeof(RandomClass).ToMessageTypeName(),
            Scope = RoutingScope.TypeName
        };
        rule.ToString().ShouldBe("Message name is 'CoreTests.Runtime.RandomClass'");
    }

    [Fact]
    public void description_of_attribute_rule()
    {
        var rule = new Subscription
        {
            BaseOrAttributeType = typeof(RandomClassAttribute),
            Scope = RoutingScope.Attribute
        };
        rule.ToString().ShouldBe("Message type is decorated with 'CoreTests.Runtime.RandomClassAttribute' or a derived type");
    }

    [Fact]
    public void description_of_all_types()
    {
        var rule = Subscription.All();
        rule.ToString().ShouldBe("All Messages");
    }

    [Fact]
    public void negative_assembly_test()
    {
        var rule = new Subscription(typeof(RandomClass).Assembly);
        rule.Matches(typeof(Message1)).ShouldBeFalse();
        rule.Matches(typeof(Message2)).ShouldBeFalse();
        rule.Matches(GetType()).ShouldBeTrue();
    }

    [Fact]
    public void negative_namespace_test()
    {
        var rule = new Subscription
        {
            Scope = RoutingScope.Namespace,
            Match = typeof(RedMessage1).Namespace
        };

        rule.Matches(typeof(GreenMessage1)).ShouldBeFalse();
        rule.Matches(typeof(GreenMessage2)).ShouldBeFalse();
        rule.Matches(typeof(GreenMessage3)).ShouldBeFalse();
    }

    [Fact]
    public void positive_assembly_test()
    {
        var rule = new Subscription(typeof(NewUser).Assembly);

        rule.Matches(typeof(NewUser)).ShouldBeTrue();
        rule.Matches(typeof(EditUser)).ShouldBeTrue();
        rule.Matches(typeof(DeleteUser)).ShouldBeTrue();
    }

    [Fact]
    public void negative_attribute_test()
    {
        var rule = new Subscription
        {
            Scope = RoutingScope.Attribute,
            BaseOrAttributeType = typeof(GreenAttribute)
        };

        rule.Matches(typeof(GreenMessage1)).ShouldBeFalse();
        rule.Matches(typeof(GreenMessage2)).ShouldBeFalse();
        rule.Matches(typeof(GreenMessage3)).ShouldBeFalse();
    }

    [Fact]
    public void positive_namespace_test()
    {
        var rule = new Subscription
        {
            Scope = RoutingScope.Namespace,
            Match = typeof(RedMessage1).Namespace
        };

        rule.Matches(typeof(RedMessage1)).ShouldBeTrue();
        rule.Matches(typeof(RedMessage2)).ShouldBeTrue();
        rule.Matches(typeof(RedMessage3)).ShouldBeTrue();
    }

    [Fact]
    public void positive_attribute_test()
    {
        var rule = new Subscription
        {
            Scope = RoutingScope.Attribute,
            BaseOrAttributeType = typeof(RedAttribute)
        };

        rule.Matches(typeof(RedMessage1)).ShouldBeTrue();
        rule.Matches(typeof(RedMessage2)).ShouldBeTrue();
        rule.Matches(typeof(RedMessage3)).ShouldBeTrue();
    }
}

[RandomClass]
public class RandomClass;

public class RandomClassAttribute : Attribute;