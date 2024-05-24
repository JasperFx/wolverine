using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using NSubstitute;
using Wolverine.Codegen;
using Xunit;

namespace CoreTests.Codegen;

public class ServiceVariablesTests
{
    private readonly IMethodVariables theMethod = Substitute.For<IMethodVariables>();
    private readonly ServiceVariables theVariables;

    public ServiceVariablesTests()
    {
        theVariables = new ServiceVariables(theMethod, new List<InjectedSingleton>());
    }

    [Fact]
    public void resolve_variable_for_IServiceProvider_when_inner_does_not_have_it()
    {
        var provider = theVariables.As<IMethodVariables>().FindVariable(typeof(IServiceProvider));
        provider.Creator.ShouldBeOfType<ScopedContainerCreation>();
        provider.VariableType.ShouldBe(typeof(IServiceProvider));
    }
    
    [Fact]
    public void try_resolve_variable_for_IServiceProvider_when_inner_does_not_have_it()
    {
        var provider = theVariables.As<IMethodVariables>().TryFindVariable(typeof(IServiceProvider), VariableSource.All);
        provider.Creator.ShouldBeOfType<ScopedContainerCreation>();
        provider.VariableType.ShouldBe(typeof(IServiceProvider));
    }
    
    [Fact]
    public void resolve_variable_for_IServiceProvider_when_inner_has_it_already()
    {
        var inner = new Variable(typeof(IServiceProvider));
        theMethod.TryFindVariable(typeof(IServiceProvider), VariableSource.NotServices)
            .Returns(inner);
        
        var provider = theVariables.As<IMethodVariables>().FindVariable(typeof(IServiceProvider));
        provider.ShouldBeSameAs(inner);
    }
    
    [Fact]
    public void try_resolve_variable_for_IServiceProvider_when_inner_has_it_already()
    {
        var inner = new Variable(typeof(IServiceProvider));
        theMethod.TryFindVariable(typeof(IServiceProvider), VariableSource.NotServices)
            .Returns(inner);
        
        var provider = theVariables.As<IMethodVariables>().TryFindVariable(typeof(IServiceProvider), VariableSource.All);
        provider.ShouldBeSameAs(inner);
    }
}