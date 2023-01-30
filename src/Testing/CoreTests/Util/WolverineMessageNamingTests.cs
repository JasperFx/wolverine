using Module1;
using Wolverine.Attributes;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Util;

public class WolverineMessageNamingTests
{
    [Fact]
    public void respect_the_type_alias_attribute()
    {
        typeof(AliasedMessage).ToMessageTypeName()
            .ShouldBe("MyThing");
    }

    [Fact]
    public void use_the_types_full_name_otherwise()
    {
        typeof(MySpecialMessage).ToMessageTypeName()
            .ShouldBe(typeof(MySpecialMessage).FullName);
    }

    [Fact]
    public void use_the_version_if_it_exists()
    {
        typeof(AliasedMessage2).ToMessageTypeName()
            .ShouldBe("MyThing.V2");
    }

    [Fact]
    public void use_interface_from_interop_message_naming()
    {
        WolverineMessageNaming.AddMessageInterfaceAssembly(typeof(IInterfaceMessage).Assembly);
        
        typeof(ConcreteMessage).ToMessageTypeName().ShouldBe(typeof(IInterfaceMessage).ToMessageTypeName());
    }
}

public class ConcreteMessage : IInterfaceMessage
{
    public string Name { get; set; }
}

[MessageIdentity("MyThing")]
public class AliasedMessage
{
}

[MessageIdentity("MyThing", Version = 2)]
public class AliasedMessage2
{
}

public class MySpecialMessage
{
}