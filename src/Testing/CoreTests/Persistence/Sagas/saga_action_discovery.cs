using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Persistence.Sagas;

public class saga_action_discovery : IntegrationContext
{
    private readonly DefaultApp _fixture;
    private readonly ITestOutputHelper _output;

    public saga_action_discovery(DefaultApp @default, ITestOutputHelper output) : base(@default)
    {
        _fixture = @default;
        _output = output;
    }

    private new HandlerChain chainFor<T>()
    {
        var handlerChain = _fixture.ChainFor<T>();
        if (handlerChain != null)
        {
            _output.WriteLine(handlerChain.SourceCode);
        }

        return handlerChain;
    }

    [Fact]
    public void finds_actions_on_saga_state_handler_classes()
    {
        chainFor<SagaMessage2>().ShouldNotBeNull();
    }

    [Fact]
    public void automatic_audit_of_saga_message_saga_id()
    {
        // Force it to compile
        var handler = Handlers.HandlerFor<SagaMessage2>();
        
        var handlerChain = chainFor<SagaMessage2>();
        handlerChain.SourceCode.ShouldContain("System.Diagnostics.Activity.Current?.SetTag(\"Id\", sagaMessage2.Id);");
        
        handlerChain.AuditedMembers.Single().MemberName
            .ShouldBe(nameof(SagaMessage2.Id));
    }
    
    [Fact]
    public void automatic_audit_of_saga_message_saga_id_with_override()
    {
        // Force it to compile
        var handler = Handlers.HandlerFor<SagaMessage1>();
        
        var handlerChain = chainFor<SagaMessage1>();
        handlerChain.SourceCode.ShouldContain("System.Diagnostics.Activity.Current?.SetTag(\"id\", sagaMessage1.Id);");
        
        handlerChain.AuditedMembers.Single().MemberName
            .ShouldBe("StreamId");

    }

    [Fact]
    public void finds_actions_on_saga_state_orchestrates_methods()
    {
        chainFor<SagaMessage1>().ShouldNotBeNull();
    }

    [Fact]
    public void applies_the_saga_id_member_as_an_identity_member()
    {
        var handlerChain = chainFor<SagaMessage1>();
        
        handlerChain.TryInferMessageIdentity(out var property).ShouldBeTrue();
        
        property
            .Name.ShouldBe(nameof(SagaMessage1.Id));
        
        handlerChain.InputType().ShouldBe(typeof(SagaMessage1));
    }

    [Fact]
    public void finds_actions_on_saga_state_start_methods()
    {
        chainFor<SagaStarter>().ShouldNotBeNull();
    }
}

public class MySagaStateGuy : Saga
{
    public Guid Id { get; set; }

    public void Orchestrates(SagaMessage1 message)
    {
    }

    public void Handle(SagaMessage2 message)
    {
    }

    public void Start(SagaStarter starter)
    {
    }
}

public class SagaStarter : Message3;

public class SagaMessage1
{
    [Audit("StreamId")]
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class SagaMessage2 : Message2;
