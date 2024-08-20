using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Codegen;
using ServiceFamily = Wolverine.Codegen.ServiceFamily;

namespace Wolverine.Runtime;

public class ServiceContainer : IServiceProviderIsService, IServiceContainer
{
    public static ServiceContainer Empty()
    {
        var services = new ServiceCollection();
        return new ServiceContainer(services, services.BuildServiceProvider());
    }
    
    private readonly IServiceProviderIsService _serviceChecker;
    private ImHashMap<Type, ServicePlan> _defaults = ImHashMap<Type, ServicePlan>.Empty;
    
    private ImHashMap<ServiceDescriptor, ServicePlan> _plans = ImHashMap<ServiceDescriptor, ServicePlan>.Empty;
    
    private ImHashMap<Type, ServiceFamily> _families = ImHashMap<Type, ServiceFamily>.Empty;
    
    
    private readonly IServiceProvider _provider;
    private readonly IServiceCollection _collection;

    public ServiceContainer(IServiceCollection services, IServiceProvider provider)
    {
        _collection = services;
        var families = services.GroupBy(x => x.ServiceType)
            .Select(x => new ServiceFamily(x.Key, x));

        foreach (var family in families)
        {
            _families = _families.AddOrUpdate(family.ServiceType, family);
        }
        
        _serviceChecker = provider as IServiceProviderIsService ?? provider.GetService<IServiceProviderIsService>() ?? this;
        _provider = provider;
    }

    public IServiceProvider Services => _provider;
    

    public IReadOnlyList<ServiceDescriptor> RegistrationsFor(Type serviceType)
    {
        return findFamily(serviceType).Services;
    }

    public IReadOnlyList<ServiceDescriptor> RegistrationsFor<T>()
    {
        return RegistrationsFor(typeof(T));
    }

    public bool HasRegistrationFor(Type serviceType)
    {
        return RegistrationsFor(serviceType).Any();
    }

    public bool HasRegistrationFor<T>()
    {
        return HasRegistrationFor(typeof(T));
    }

    public ServiceDescriptor? DefaultFor(Type serviceType)
    {
#if NET8_0_OR_GREATER
        return RegistrationsFor(serviceType).LastOrDefault(x => !x.IsKeyedService);
        #else
        return RegistrationsFor(serviceType).LastOrDefault();
#endif
    }

    public ServiceDescriptor? DefaultFor<T>()
    {
        return DefaultFor(typeof(T));
    }

    public IEnumerable<Frame> TryCreateConstructorFrames(IEnumerable<MethodCall> calls)
    {
        if (calls.All(x => x.Method.IsStatic)) return new List<Frame>();

        var list = calls.Select(x => x.HandlerType).Where(x => !x.IsStatic()).Select(handlerType =>
        {
            var serviceDescriptor = new ServiceDescriptor(handlerType, handlerType, ServiceLifetime.Scoped);
            if (ConstructorPlan.TryBuildPlan(new List<ServiceDescriptor>(), serviceDescriptor, this, out var plan))
            {
                if (plan is ConstructorPlan ctor)
                {
                    return new ConstructorFrame(handlerType, ctor.Constructor);
                }
            }

            throw new NotSupportedException(
                $"Handler type {handlerType.FullNameInCode()} does not have a suitable, public constructor for Wolverine or is missing registered dependencies");
        
        }).ToList();

        foreach (var group in list.GroupBy(x => x.Variable.Usage).Where(x => x.Count() > 1))
        {
            var index = 0;
            foreach (var frame in group)
            {
                frame.Variable.OverrideName(frame.Variable.Usage + (++index));
            }
        }

        return list;
    }

    public T GetInstance<T>() where T : notnull
    {
        return _provider.GetRequiredService<T>();
    }

    public IReadOnlyList<T> GetAllInstances<T>()
    {
        return _provider.GetServices<T>().ToList();
    }

    public IReadOnlyList<ServiceDescriptor> FindMatchingServices(Func<Type, bool> filter)
    {
        return _collection.Where(x => filter(x.ServiceType)).ToList();
    }

    public IEnumerable<Type> ServiceDependenciesFor(Type serviceType)
    {
        try
        {
            var family = findFamily(serviceType);
            if (family.Default == null)
            {
                return Array.Empty<Type>();
            }

            var list = new List<ServiceDescriptor>();
            var plan = planFor(family.Default, list);
            return plan.FindDependencies().Distinct().ToArray();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Wolverine encountered an error while trying to find service dependencies for type {serviceType.FullNameInCode()}", e);
        }
    }

    bool IServiceProviderIsService.IsService(Type serviceType)
    {
        return false;
    }

    private ServicePlan planFor(ServiceDescriptor descriptor, List<ServiceDescriptor> trail)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }
        
        if (_plans.TryFind(descriptor, out var plan)) return plan;

        var family = findFamily(descriptor.ServiceType);
        plan = family.BuildPlan(this, descriptor, trail);

        _plans = _plans.AddOrUpdate(descriptor, plan);
        return plan;
    }

    public bool CouldResolve(Type type) => CouldResolve(type, new());

    public bool CouldResolve(Type type, List<ServiceDescriptor> trail)
    {
        if (_defaults.TryFind(type, out var plan))
        {
            return !(plan is InvalidPlan);
        }
        
        if (_defaults.Contains(type)) return true;

        if (IsEnumerable(type))
        {
            return true;
        }
        
        if (_serviceChecker.IsService(type)) return true;

        if (type.IsConcreteWithDefaultCtor())
        {
            return true;
        }
        
        var descriptor = findDefaultDescriptor(type);
        if (descriptor == null) return false;
        
        plan = planFor(descriptor, trail);
        return plan is not InvalidPlan;
    }
    
    public static bool IsEnumerable(Type type)
    {
        if (type.IsArray) return true;

        if (!type.IsGenericType) return false;

        if (type.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return true;
        if (type.GetGenericTypeDefinition() == typeof(IList<>)) return true;
        if (type.GetGenericTypeDefinition() == typeof(List<>)) return true;
        if (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) return true;

        return false;
    }
    
    private ServiceFamily findFamily(Type serviceType)
    {
        if (_families.TryFind(serviceType, out var family)) return family;

        if (serviceType == typeof(IServiceProvider))
        {
            family = new ServiceProviderFamily();
            _families = _families.AddOrUpdate(serviceType, family);
            return family;
        }
        
        if (IsEnumerable(serviceType))
        {
            family = new ArrayFamily(serviceType);
            _families = _families.AddOrUpdate(serviceType, family);
            return family;
        }

        if (serviceType.IsGenericType && serviceType.IsNotConcrete())
        {
            var templateType = serviceType.GetGenericTypeDefinition();
            var templatedParameterTypes = serviceType.GetGenericArguments();
        
            if (_families.TryFind(templateType, out var generic))
            {
                family = generic.Close(templatedParameterTypes);
                _families = _families.AddOrUpdate(serviceType, family);
                return family;
            }
            else
            {
                // Memoize the "miss"
                family = new ServiceFamily(serviceType, ArraySegment<ServiceDescriptor>.Empty);
                _families = _families.AddOrUpdate(serviceType, family);
                return family;
            }
        }

        if ((serviceType.IsPublic || serviceType.IsNestedPublic)  && serviceType.IsConcrete())
        {
            var descriptor = new ServiceDescriptor(serviceType, serviceType, ServiceLifetime.Scoped);
            family = new ServiceFamily(serviceType, [descriptor]);
            _families = _families.AddOrUpdate(serviceType, family);

            return family;
        }

        return new ServiceFamily(serviceType, []);
    }

    private ServiceDescriptor? findDefaultDescriptor(Type type)
    {
        var family = findFamily(type);
        return family.Default;
    }

    internal ServicePlan? FindDefault(Type type, List<ServiceDescriptor> trail)
    {
        if (_defaults.TryFind(type, out var plan)) return plan;

        var family = findFamily(type);
        plan = family.BuildDefaultPlan(this, trail);

        // Memoize the "miss" as well
        _defaults = _defaults.AddOrUpdate(type, plan);
        
        return plan;
    }

    internal IReadOnlyList<ServicePlan> FindAll(Type serviceType, List<ServiceDescriptor> trail)
    {
        return findFamily(serviceType).Services.Select(descriptor => planFor(descriptor, trail)).ToArray();
    }

    public object BuildFromType(Type concreteType)
    {
        var constructor = concreteType.GetConstructors().Single();
        var dependencies = constructor.GetParameters().Select(x => _provider.GetService(x.ParameterType)).ToArray();
        return Activator.CreateInstance(concreteType, dependencies);
    }

    public bool HasMultiplesOf(Type variableType)
    {
        return findFamily(variableType).Services.Count > 1;
    }
    
    /// <summary>
    /// Polyfill to make IServiceProvider work like Lamar's ability
    /// to create unknown concrete types
    /// </summary>
    /// <param name="provider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T QuickBuild<T>()
    {
        return (T)QuickBuild(typeof(T));
    }
    
    /// <summary>
    /// Polyfill to make IServiceProvider work like Lamar's ability
    /// to create unknown concrete types
    /// </summary>
    /// <param name="provider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public object QuickBuild(Type concreteType)
    {
        var constructor = concreteType.GetConstructors().Single();
        var args = constructor
            .GetParameters()
            .Select(x => _provider.GetService(x.ParameterType))
            .ToArray();

        return Activator.CreateInstance(concreteType, args);
    }
}