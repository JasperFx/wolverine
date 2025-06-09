using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

internal class ServiceLocationPlan : ServicePlan
{
    public ServiceLocationPlan(ServiceDescriptor descriptor) : base(descriptor)
    {

    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return true;
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        if (Descriptor.IsKeyedService)
        {
            if (Descriptor.KeyedImplementationFactory != null)
            {
                return
                    $"The service registration for {Descriptor.ServiceType.FullNameInCode()} is an 'opaque' lambda factory with the {Descriptor.Lifetime} lifetime and requires service location";
            }

            return $"Concrete type {Descriptor.KeyedImplementationType.FullNameInCode()} is not public, so requires service location";
        }
        
        if (Descriptor.ImplementationFactory != null)
        {
            return
                $"The service registration for {Descriptor.ServiceType.FullNameInCode()} is an 'opaque' lambda factory with the {Descriptor.Lifetime} lifetime and requires service location";
        }

        return $"Concrete type {Descriptor.ImplementationType.FullNameInCode()} is not public, so requires service location";
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        return new GetServiceFromScopedContainerFrame(resolverVariables.ServiceProvider, Descriptor.ServiceType)
            .Variable;
    }
}