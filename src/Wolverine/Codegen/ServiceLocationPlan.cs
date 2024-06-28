using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

internal class ServiceLocationPlan : ServicePlan
{
    public ServiceLocationPlan(ServiceDescriptor descriptor) : base(descriptor)
    {
#if NET8_0_OR_GREATER
        if (descriptor.IsKeyedService)
        {
            ArgumentNullException.ThrowIfNull(descriptor.KeyedImplementationFactory);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(descriptor.ImplementationFactory);
        }
#endif
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return true;
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
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