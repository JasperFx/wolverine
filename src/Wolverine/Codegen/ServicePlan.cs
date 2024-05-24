using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

// Honestly, this needs to be an abstract type with a couple different
// sub types for scoped or transient 
internal abstract class ServicePlan
{
    public ServiceDescriptor Descriptor { get; }

    public ServicePlan(ServiceDescriptor descriptor)
    {
        Descriptor = descriptor;

        ServiceType = descriptor.ServiceType;
    }
    
    public Type ServiceType { get; }

    public ServiceLifetime Lifetime => Descriptor.Lifetime;

    public bool RequiresServiceProvider(IMethodVariables method)
    {
        var fromOutside = method.TryFindVariable(ServiceType, VariableSource.NotServices);
        if (fromOutside != null && fromOutside is not StandInVariable) return false;

        return requiresServiceProvider(method);
    }

    protected abstract bool requiresServiceProvider(IMethodVariables method);
    
    public abstract string WhyRequireServiceProvider(IMethodVariables method);

    public abstract Variable CreateVariable(ServiceVariables resolverVariables);

    public override string ToString()
    {
        return $"{nameof(Descriptor)}: {Descriptor}";
    }

    public virtual IEnumerable<Type> FindDependencies() => Array.Empty<Type>();
}