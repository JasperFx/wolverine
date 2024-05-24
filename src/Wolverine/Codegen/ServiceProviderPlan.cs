using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Codegen;

internal class ServiceProviderFamily : ServiceFamily
{
    public ServiceProviderFamily() : base(typeof(IServiceProvider), Array.Empty<ServiceDescriptor>())
    {
        
    }

    public override ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        return new ServiceProviderPlan(new ServiceDescriptor(typeof(IServiceProvider), typeof(ServiceDescriptor),
            ServiceLifetime.Scoped));
    }
}

internal class ServiceProviderPlan : ServicePlan
{
    public ServiceProviderPlan(ServiceDescriptor descriptor) : base(descriptor)
    {
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return true;
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        return "Your code is directly using IServiceProvider";
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        throw new NotImplementedException();
    }
}