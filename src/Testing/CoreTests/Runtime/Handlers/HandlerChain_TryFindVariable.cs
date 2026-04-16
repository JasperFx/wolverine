using JasperFx.CodeGeneration.Frames;
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
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.InputMember, typeof(Guid), out var variable)
            .ShouldBeTrue();

        variable.ShouldBeOfType<MessageMemberVariable>()
            .Member.Name.ShouldBe("Id");
    }

    [Fact]
    public void miss_on_type()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.InputMember, typeof(int), out var variable)
            .ShouldBeFalse();
    }

    [Fact]
    public void miss_on_member_name()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable("wrong", ValueSource.InputMember, typeof(Guid), out var variable)
            .ShouldBeFalse();
    }

    [Fact]
    public void for_matching_member_name_on_name_and_type_and_anything_is_the_source()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.Anything, typeof(Guid), out var variable)
            .ShouldBeTrue();

        variable.ShouldBeOfType<MessageMemberVariable>()
            .Member.Name.ShouldBe("Id");
    }

    [Fact]
    public void miss_on_unsupported_value_sources()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable(nameof(CreateThing.Id), ValueSource.RouteValue, typeof(Guid), out var variable)
            .ShouldBeFalse();
    }

    [Fact]
    public void header_source_creates_envelope_header_variable()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable("X-Tenant-Id", ValueSource.Header, typeof(string), out var variable)
            .ShouldBeTrue();

        variable.ShouldNotBeNull();
        variable.Creator.ShouldBeOfType<ReadEnvelopeHeaderFrame>();
    }

    [Fact]
    public void header_source_with_typed_guid_value()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable("X-Stream-Id", ValueSource.Header, typeof(Guid), out var variable)
            .ShouldBeTrue();

        variable.ShouldNotBeNull();
        variable.VariableType.ShouldBe(typeof(Guid));
        variable.Creator.ShouldBeOfType<ReadEnvelopeHeaderFrame>();
    }

    [Fact]
    public void header_source_with_typed_int_value()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable("X-Count", ValueSource.Header, typeof(int), out var variable)
            .ShouldBeTrue();

        variable.ShouldNotBeNull();
        variable.VariableType.ShouldBe(typeof(int));
    }

    [Fact]
    public void anything_source_does_not_fall_back_to_header()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        // Header requires explicit ValueSource.Header — Anything does not include it
        // because Header always "succeeds" and would swallow all unmatched names
        chain.TryFindVariable("X-Tenant", ValueSource.Anything, typeof(string), out var variable)
            .ShouldBeFalse();
    }

    [Fact]
    public void anything_source_prefers_input_member_over_header()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        // "Name" matches a property on CreateThing, should use InputMember not Header
        chain.TryFindVariable("Name", ValueSource.Anything, typeof(string), out var variable)
            .ShouldBeTrue();

        variable.ShouldBeOfType<MessageMemberVariable>();
    }

    [Fact]
    public void claim_source_throws_in_handler_context()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        Should.Throw<InvalidOperationException>(() =>
            chain.TryFindVariable("sub", ValueSource.Claim, typeof(string), out _))
            .Message.ShouldContain("HTTP endpoints");
    }

    [Fact]
    public void method_source_discovers_static_method_on_handler_type()
    {
        var chain = HandlerChain.For<HandlerWithStaticMethod>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable("ResolveId", ValueSource.Method, typeof(Guid), out var variable)
            .ShouldBeTrue();

        variable.ShouldNotBeNull();
        variable.VariableType.ShouldBe(typeof(Guid));
    }

    [Fact]
    public void method_source_discovers_static_method_on_base_type()
    {
        var chain = HandlerChain.For<DerivedHandler>(x => x.Handle(null!), new HandlerGraph());

        chain.TryFindVariable("ResolveId", ValueSource.Method, typeof(Guid), out var variable)
            .ShouldBeTrue();

        variable.ShouldNotBeNull();
        variable.VariableType.ShouldBe(typeof(Guid));
    }

    [Fact]
    public void method_source_throws_when_method_not_found()
    {
        var chain = HandlerChain.For<CreateThingHandler>(x => x.Handle(null!), new HandlerGraph());

        Should.Throw<InvalidOperationException>(() =>
            chain.TryFindVariable("NonExistentMethod", ValueSource.Method, typeof(Guid), out _))
            .Message.ShouldContain("NonExistentMethod");
    }
}

public class WolverineParameterAttribute_convenience_properties
{
    private class TestAttribute : WolverineParameterAttribute
    {
        public override JasperFx.CodeGeneration.Model.Variable Modify(
            Wolverine.Configuration.IChain chain, System.Reflection.ParameterInfo parameter,
            JasperFx.IServiceContainer container, JasperFx.CodeGeneration.GenerationRules rules)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public void from_header_sets_value_source_and_argument_name()
    {
        var att = new TestAttribute { FromHeader = "X-Tenant-Id" };
        att.ValueSource.ShouldBe(ValueSource.Header);
        att.ArgumentName.ShouldBe("X-Tenant-Id");
    }

    [Fact]
    public void from_route_sets_value_source_and_argument_name()
    {
        var att = new TestAttribute { FromRoute = "orderId" };
        att.ValueSource.ShouldBe(ValueSource.RouteValue);
        att.ArgumentName.ShouldBe("orderId");
    }

    [Fact]
    public void from_claim_sets_value_source_and_argument_name()
    {
        var att = new TestAttribute { FromClaim = "sub" };
        att.ValueSource.ShouldBe(ValueSource.Claim);
        att.ArgumentName.ShouldBe("sub");
    }

    [Fact]
    public void from_method_sets_value_source_and_argument_name()
    {
        var att = new TestAttribute { FromMethod = "ResolveId" };
        att.ValueSource.ShouldBe(ValueSource.Method);
        att.ArgumentName.ShouldBe("ResolveId");
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

public class HandlerWithStaticMethod
{
    public static Guid ResolveId() => Guid.NewGuid();

    public void Handle(CreateThing command)
    {
        // Nothing
    }
}

public class BaseHandlerWithMethod
{
    public static Guid ResolveId() => Guid.NewGuid();
}

public class DerivedHandler : BaseHandlerWithMethod
{
    public void Handle(CreateThing command)
    {
        // Nothing
    }
}
