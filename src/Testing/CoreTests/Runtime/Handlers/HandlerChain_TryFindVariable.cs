using JasperFx.CodeGeneration.Services;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class HandlerChain_TryFindVariable
{
    [Fact]
    public void for_matching_member_name_on_name_and_type()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        
        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.InputMember, typeof(Guid), out var variable)
            .ShouldBeTrue();
        
        variable.ShouldBeOfType<MessageMemberVariable>()
            .Member.Name.ShouldBe("Id");
    }
    
    [Fact]
    public void miss_on_type()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        
        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.InputMember, typeof(int), out var variable)
            .ShouldBeFalse();
    }
    
    [Fact]
    public void miss_on_member_name()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        
        chain.TryFindVariable("wrong", ValueSource.InputMember, typeof(Guid), out var variable)
            .ShouldBeFalse();
    }
    
    [Fact]
    public void for_matching_member_name_on_name_and_type_and_anything_is_the_source()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        
        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.Anything, typeof(Guid), out var variable)
            .ShouldBeTrue();
        
        variable.ShouldBeOfType<MessageMemberVariable>()
            .Member.Name.ShouldBe("Id");
    }
    
    [Fact]
    public void miss_on_unsupported_value_sources()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        
        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.RouteValue, typeof(Guid), out var variable)
            .ShouldBeFalse();
    }

}

public record CreateThing(Guid Id, string Name, string Color);

public class CreateThingHandler
{
    public void Handle(CreateThing command)
    {
        // Nothing
    }
}
