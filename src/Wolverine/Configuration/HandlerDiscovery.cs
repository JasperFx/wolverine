using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

public sealed class HandlerDiscovery
{
    private readonly IList<Type> _explicitTypes = new List<Type>();

    private readonly ActionMethodFilter _methodFilters;
    private readonly HandlerTypeFilter _validTypeFilter = new();
    private readonly CompositeFilter<Type> _typeFilters = new();

    private readonly string[] _validMethods =
    {
        HandlerChain.Handle, HandlerChain.Handles, HandlerChain.Consume, HandlerChain.Consumes, SagaChain.Orchestrate,
        SagaChain.Orchestrates, SagaChain.Start, SagaChain.Starts, SagaChain.StartOrHandle, SagaChain.StartsOrHandles,
        SagaChain.NotFound
    };

    private bool _conventionalDiscoveryDisabled;

    public HandlerDiscovery()
    {
        var validMethods = _validMethods.Concat(_validMethods.Select(x => x + "Async"))
            .ToArray();
        
        _methodFilters = new ActionMethodFilter();

        _methodFilters.Includes += m => validMethods.Contains(m.Name);
        _methodFilters.Includes += m => m.HasAttribute<WolverineHandlerAttribute>();
        
        IncludeClassesSuffixedWith(HandlerChain.HandlerSuffix);
        IncludeClassesSuffixedWith(HandlerChain.ConsumerSuffix);

        IncludeTypes(x => x.CanBeCastTo<Saga>());
        IncludeTypes(x => x.CanBeCastTo<IWolverineHandler>());
        IncludeTypes(x => x.HasAttribute<WolverineHandlerAttribute>());

        _typeFilters.Excludes += t => t.HasAttribute<WolverineIgnoreAttribute>();
    }

    internal IList<Assembly> Assemblies { get; } = new List<Assembly>();

    /// <summary>
    /// Disables *all* conventional discovery of message handlers from type scanning. This is mostly useful for
    /// testing scenarios or folks who just really want to have full control over everything!
    /// </summary>
    public HandlerDiscovery DisableConventionalDiscovery(bool value = true)
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
            .Where(x => _validTypeFilter.Matches(x))
            .Where(x => !x.HasAttribute<WolverineIgnoreAttribute>())
            .Where(x => _typeFilters.Matches(x))
            .Concat(_explicitTypes)
            .Distinct()
            .SelectMany(actionsFromType).ToArray();

        return types;
    }

    private IEnumerable<(Type, MethodInfo)> actionsFromType(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
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
    public HandlerDiscovery IncludeAssembly(Assembly assembly)
    {
        Assemblies.Add(assembly);
        return this;
    }

    /// <summary>
    ///     Find Handlers from concrete classes whose names ends with the suffix
    /// </summary>
    /// <param name="suffix"></param>
    public HandlerDiscovery IncludeClassesSuffixedWith(string suffix)
    {
        return IncludeTypesNamed(x => x.EndsWith(suffix));
    }

    /// <summary>
    ///     Find Handler classes based on the Type name filter supplied
    /// </summary>
    /// <param name="filter"></param>
    public HandlerDiscovery IncludeTypesNamed(Func<string, bool> filter)
    {
        return IncludeTypes(type => filter(type.Name));
    }

    /// <summary>
    ///     Find Handlers on types that match on the provided filter
    /// </summary>
    public HandlerDiscovery IncludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Includes += filter;
        return this;
    }

    /// <summary>
    ///     Find Handlers on concrete types assignable to T
    /// </summary>
    public HandlerDiscovery IncludeTypesImplementing<T>()
    {
        IncludeTypes(type => !type.IsOpenGeneric() && type.IsConcreteTypeOf<T>());
        return this;
    }

    /// <summary>
    ///     Exclude types that match on the provided filter for finding Handlers
    /// </summary>
    public HandlerDiscovery ExcludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Excludes += filter;
        return this;
    }

    /// <summary>
    ///     Handlers that match on the provided filter will NOT be added to the runtime.
    /// </summary>
    public HandlerDiscovery ExcludeMethods(Func<MethodInfo, bool> filter)
    {
        _methodFilters.Excludes += filter;
        return this;
    }

    /// <summary>
    ///     Include a single type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public HandlerDiscovery IncludeType<T>()
    {
        return IncludeType(typeof(T));
    }

    /// <summary>
    ///     Include a single handler type
    /// </summary>
    /// <param name="type"></param>
    public HandlerDiscovery IncludeType(Type type)
    {
        if (type.IsNotPublic)
        {
            throw new ArgumentOutOfRangeException(nameof(type),
                "Handler types must be public, concrete, and closed (not generic) types");
        }
        
        if (!type.IsStatic() && (type.IsNotConcrete() || type.IsOpenGeneric()))
        {
            throw new ArgumentOutOfRangeException(nameof(type),
                "Handler types must be public, concrete, and closed (not generic) types");
        }

        _explicitTypes.Fill(type);

        return this;
    }
}