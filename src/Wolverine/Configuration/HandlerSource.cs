using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.TypeDiscovery;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

public sealed class HandlerSource
{
    private readonly IList<Type> _explicitTypes = new List<Type>();

    private readonly ActionMethodFilter _methodFilters;
    private readonly CompositeFilter<Type> _typeFilters = new();

    private readonly string[] _validMethods =
    {
        HandlerChain.Handle, HandlerChain.Handles, HandlerChain.Consume, HandlerChain.Consumes, SagaChain.Orchestrate,
        SagaChain.Orchestrates, SagaChain.Start, SagaChain.Starts, SagaChain.StartOrHandle, SagaChain.StartsOrHandles,
        SagaChain.NotFound
    };

    private bool _conventionalDiscoveryDisabled;

    public HandlerSource()
    {
        var validMethods = _validMethods.Concat(_validMethods.Select(x => x + "Async"))
            .ToArray();
        
        _methodFilters = new ActionMethodFilter();
        _methodFilters.Excludes += m => m.HasAttribute<WolverineIgnoreAttribute>();

        _methodFilters.Includes += m => validMethods.Contains(m.Name);

        IncludeClassesSuffixedWith(HandlerChain.HandlerSuffix);
        IncludeClassesSuffixedWith(HandlerChain.ConsumerSuffix);

        IncludeTypes(x => x.CanBeCastTo<Saga>());

        _typeFilters.Excludes += t => t.HasAttribute<WolverineIgnoreAttribute>();
    }

    internal IList<Assembly> Assemblies { get; } = new List<Assembly>();

    /// <summary>
    ///     Disable all conventional discovery of message handlers
    /// </summary>
    public HandlerSource DisableConventionalDiscovery(bool value = true)
    {
        _conventionalDiscoveryDisabled = value;
        return this;
    }

    internal (Type, MethodInfo)[] FindCalls(WolverineOptions options)
    {
        if (_conventionalDiscoveryDisabled)
        {
            return _explicitTypes.SelectMany(actionsFromType).ToArray();
        }

        if (options.ApplicationAssembly == null)
        {
            return Array.Empty<(Type, MethodInfo)>();
        }

        Assemblies.Fill(options.ApplicationAssembly);

        var types = Assemblies.SelectMany(x => x.ExportedTypes)
            .Where(x => x.IsStatic() || (x.IsConcrete() && !x.IsOpenGeneric()))
            .Where(x => _typeFilters.Matches(x))
            .Where(x => !x.HasAttribute<WolverineIgnoreAttribute>())
            .Concat(_explicitTypes)
            .Distinct()
            .SelectMany(actionsFromType).ToArray();

        return types;
    }

    private IEnumerable<(Type, MethodInfo)> actionsFromType(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
            .Where(x => !x.HasAttribute<WolverineIgnoreAttribute>())
            .Where(x => x.DeclaringType != typeof(object)).ToArray()
            .Where(_methodFilters.Matches)
            .Where(HandlerCall.IsCandidate)
            .Select(m => (type, m));
    }

    /// <summary>
    ///     Find Handlers from concrete classes from the given
    ///     assembly
    /// </summary>
    /// <param name="assembly"></param>
    public void IncludeAssembly(Assembly assembly)
    {
        Assemblies.Add(assembly);
    }

    /// <summary>
    ///     Find Handlers from concrete classes whose names ends with the suffix
    /// </summary>
    /// <param name="suffix"></param>
    public void IncludeClassesSuffixedWith(string suffix)
    {
        IncludeTypesNamed(x => x.EndsWith(suffix));
    }

    /// <summary>
    ///     Find Handler classes based on the Type name filter supplied
    /// </summary>
    /// <param name="filter"></param>
    public void IncludeTypesNamed(Func<string, bool> filter)
    {
        IncludeTypes(type => filter(type.Name));
    }

    /// <summary>
    ///     Find Handlers on types that match on the provided filter
    /// </summary>
    public void IncludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Includes += filter;
    }

    /// <summary>
    ///     Find Handlers on concrete types assignable to T
    /// </summary>
    public void IncludeTypesImplementing<T>()
    {
        IncludeTypes(type => !type.IsOpenGeneric() && type.IsConcreteTypeOf<T>());
    }

    /// <summary>
    ///     Exclude types that match on the provided filter for finding Handlers
    /// </summary>
    public void ExcludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Excludes += filter;
    }

    /// <summary>
    ///     Handlers that match on the provided filter will NOT be added to the runtime.
    /// </summary>
    public void ExcludeMethods(Func<MethodInfo, bool> filter)
    {
        _methodFilters.Excludes += filter;
    }

    /// <summary>
    ///     Exclude any types that are not concrete
    /// </summary>
    public void ExcludeNonConcreteTypes()
    {
        _typeFilters.Excludes += type => !type.IsConcrete();
    }

    /// <summary>
    ///     Include a single type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void IncludeType<T>()
    {
        _explicitTypes.Fill(typeof(T));
    }

    /// <summary>
    ///     Include a single handler type
    /// </summary>
    /// <param name="type"></param>
    public void IncludeType(Type type)
    {
        _explicitTypes.Fill(type);
    }
}