using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wolverine.Codegen;
using Xunit;

namespace CoreTests.Codegen;

public class ServiceLocationPlanTests
{
    private readonly IMethodVariables theVariables = Substitute.For<IMethodVariables>();

    private readonly ServiceLocationPlan theServiceLocationPlan =
        new(new ServiceDescriptor(typeof(IWidget), s => new AWidget(), ServiceLifetime.Scoped));

    [Fact]
    public void requires_service_provider()
    {
        theServiceLocationPlan.RequiresServiceProvider(theVariables).ShouldBeTrue();
    }

    [Fact]
    public void why_require_service_provider()
    {
        var why = theServiceLocationPlan.WhyRequireServiceProvider(theVariables);
        
        why.ShouldBe($"The service registration for {typeof(IWidget).FullNameInCode()} is an 'opaque' lambda factory with the {nameof(ServiceLifetime.Scoped)} lifetime and requires service location");
    }

    [Fact]
    public void build_variable()
    {
        var variables = new ServiceVariables();

        var variable = theServiceLocationPlan.CreateVariable(variables);

        variable.Creator.ShouldBeOfType<GetServiceFromScopedContainerFrame>();
        variable.VariableType.ShouldBe(typeof(IWidget));

    }
}