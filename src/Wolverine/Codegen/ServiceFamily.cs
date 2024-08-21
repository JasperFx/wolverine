using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Codegen;

internal class ServiceFamily
{
    public Type ServiceType { get; }
    public IReadOnlyList<ServiceDescriptor> Services { get; }

    public ServiceFamily(Type serviceType, IEnumerable<ServiceDescriptor> services)
    {
        ServiceType = serviceType;
        Services = services.ToArray();
    }

#if NET8_0_OR_GREATER
    public ServiceDescriptor? Default => Services.LastOrDefault(x => !x.IsKeyedService);
    #else
    public ServiceDescriptor? Default => Services.LastOrDefault();
#endif

    public ServiceFamily Close(Type[] parameterTypes)
    {
        if (!ServiceType.IsOpenGeneric())
            throw new InvalidOperationException($"{ServiceType.FullNameInCode()} is not an open type");
        var serviceType = ServiceType.MakeGenericType(parameterTypes);
        
        var candidates = Services.Where(x => x.ImplementationType != null).Select(open =>
        {
            try
            {
                var concreteType = open.ImplementationType!.MakeGenericType(parameterTypes);
                if (concreteType.CanBeCastTo(serviceType))
                {
                    return new ServiceDescriptor(serviceType, concreteType, open.Lifetime);
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }).Where(x => x != null).ToArray();

        return new ServiceFamily(serviceType, candidates);
    }

    public virtual ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        var descriptor = Services.LastOrDefault();
        if (descriptor == null) return null;
        
        return BuildPlan(graph, descriptor, trail);
    }

    internal ServicePlan BuildPlan(ServiceContainer graph, ServiceDescriptor descriptor,
        List<ServiceDescriptor> trail)
    {
        if (trail.Contains(descriptor)) return new InvalidPlan(descriptor);
        
        if (descriptor.ServiceType.IsNotPublic)
        {
            return new ServiceLocationPlan(descriptor);
        }

#if NET8_0_OR_GREATER
        if (descriptor.IsKeyedService)
        {
            throw new NotSupportedException("Not quite able yet to support keyed implementations");
        }
#endif

        if (descriptor.Lifetime == ServiceLifetime.Singleton)
        {
            return new SingletonPlan(descriptor);
        }

        if (descriptor.ImplementationFactory != null)
        {
            return new ServiceLocationPlan(descriptor);
        }
        
        if (!descriptor.ImplementationType.IsConcrete())
        {
            // If you don't know how to create it, you can't use it, period
            return new InvalidPlan(descriptor);
        }

        if (descriptor.ImplementationType.IsNotPublic)
        {
            return new ServiceLocationPlan(descriptor);
        }
        
        if (ConstructorPlan.TryBuildPlan(trail, descriptor, graph, out var plan))
        {
            return plan;
        }

        return new InvalidPlan(descriptor);
    }
}
