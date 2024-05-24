using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wolverine.Codegen;
using Xunit;

namespace CoreTests.Codegen;

public class SingletonPlanTests
{
    private readonly SingletonPlan theSingletonPlan =
        new(new ServiceDescriptor(typeof(IWidget), typeof(AWidget), ServiceLifetime.Singleton));

    private readonly IMethodVariables theVariables = Substitute.For<IMethodVariables>();

    [Fact]
    public void requires_service_provider_is_false()
    {
        theSingletonPlan.RequiresServiceProvider(theVariables).ShouldBeFalse();
    }

    [Fact]
    public void create_variable_builds_injected_singleton()
    {
        var variable =
            theSingletonPlan.CreateVariable(new ServiceVariables(theVariables, new List<InjectedSingleton>()));

        variable.ShouldBeOfType<InjectedSingleton>()
            .Descriptor.ShouldBe(theSingletonPlan.Descriptor);
    }
}