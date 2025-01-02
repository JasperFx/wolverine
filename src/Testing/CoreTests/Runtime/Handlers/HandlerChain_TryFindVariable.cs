using Wolverine.Attributes;
using Wolverine.Codegen;
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
    
    [Theory]
    [InlineData(ValueSource.RouteValue)]
    [InlineData(ValueSource.QueryString)]
    [InlineData(ValueSource.Claim)]
    public void miss_on_unsupported_value_sources(ValueSource source)
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        
        chain.TryFindVariable(nameof(CreateThing.Id), source, typeof(Guid), out var variable)
            .ShouldBeFalse();
    }

    [Fact]
    public void automatically_use_for_header()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        chain.TryFindVariable("anything", ValueSource.Header, typeof(Guid), out var variable)
            .ShouldBeTrue();
        
        variable.Usage.ShouldBe("anything");
        variable.VariableType.ShouldBe(typeof(Guid));
        var creator = variable.Creator.ShouldBeOfType<EnvelopeHeaderValueFrame>();
        creator.HeaderName.ShouldBe("anything");
    }
    
    [Fact]  
    public void fall_through_to_use_header_on_anything()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null), new HandlerGraph());
        chain.TryFindVariable("anything", ValueSource.Anything, typeof(Guid), out var variable)
            .ShouldBeTrue();
        
        variable.Usage.ShouldBe("anything");
        variable.VariableType.ShouldBe(typeof(Guid));
        var creator = variable.Creator.ShouldBeOfType<EnvelopeHeaderValueFrame>();
        creator.HeaderName.ShouldBe("anything");
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
