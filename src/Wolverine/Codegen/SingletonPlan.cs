using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

internal class SingletonPlan : ServicePlan
{
    public SingletonPlan(ServiceDescriptor descriptor) : base(descriptor)
    {

    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return false;
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        return "It does not";
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        return new InjectedSingleton(Descriptor);
    }
}