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
    public void GetPrettyName()
    {
        typeof(string).GetPrettyName()
            .ShouldBe("String");
        typeof(List<string>).GetPrettyName()
            .ShouldBe("List<String>");
        typeof(Dictionary<int, string>).GetPrettyName()
            .ShouldBe("Dictionary<Int32,String>");
        typeof(Dictionary<int, List<string>>).GetPrettyName()
            .ShouldBe("Dictionary<Int32,List<String>>");
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

    [Fact]
    public void forward_to_another_type_by_attribute()
    {
        typeof(DifferentMessage).ToMessageTypeName()
            .ShouldBe("MyThing");
    }

    [Fact]
    public void respect_the_interop_attribute()
    {
        typeof(InteropAttributedMessage).ToMessageTypeName()
            .ShouldBe(typeof(IMessageInterface).ToMessageTypeName());
    }
}

public class ConcreteMessage : IInterfaceMessage
{
    public string Name { get; set; }
}

[MessageIdentity(typeof(AliasedMessage))]
public class DifferentMessage;


[MessageIdentity("MyThing")]
public class AliasedMessage;

[MessageIdentity("MyThing", Version = 2)]
public class AliasedMessage2;

public class MySpecialMessage;

public interface IMessageInterface;

[InteropMessage(typeof(IMessageInterface))]
public class InteropAttributedMessage : IMessageInterface;