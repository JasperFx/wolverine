using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.TypeDiscovery;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

public sealed class HandlerDiscovery
{
    private readonly IList<Type> _explicitTypes = new List<Type>();

    private readonly ActionMethodFilter _methodFilters;

    private readonly string[] _validMethods =
    {
        HandlerChain.Handle, HandlerChain.Handles, HandlerChain.Consume, HandlerChain.Consumes, SagaChain.Orchestrate,
        SagaChain.Orchestrates, SagaChain.Start, SagaChain.Starts, SagaChain.StartOrHandle, SagaChain.StartsOrHandles,
        SagaChain.NotFound
    };

    private bool _conventionalDiscoveryDisabled;

    private readonly TypeQuery _handlerQuery = new(TypeClassification.Concretes | TypeClassification.Closed);
    private readonly TypeQuery _messageQuery = new(TypeClassification.Concretes | TypeClassification.Closed);
    
    public HandlerDiscovery()
    {
        var validMethods = _validMethods.Concat(_validMethods.Select(x => x + "Async"))
            .ToArray();
        
        _methodFilters = new ActionMethodFilter();

        _methodFilters.Includes += m => validMethods.Contains(m.Name);
        _methodFilters.Includes += m => m.HasAttribute<WolverineHandlerAttribute>();
        
        _handlerQuery.Includes.WithNameSuffix(HandlerChain.HandlerSuffix);
        _handlerQuery.Includes.WithNameSuffix(HandlerChain.ConsumerSuffix);
        _handlerQuery.Includes.Implements<Saga>();
        _handlerQuery.Includes.Implements<IWolverineHandler>();
        _handlerQuery.Includes.WithAttribute<WolverineHandlerAttribute>();
        
        _handlerQuery.Excludes.WithUserDefinedCondition("Is not Public & not Static", t => !t.IsStatic() && !t.IsPublic);
        
        _handlerQuery.Excludes.WithAttribute<WolverineIgnoreAttribute>();
        
        _messageQuery.Excludes.IsStatic();
        _messageQuery.Includes.Implements<IMessage>();
        _messageQuery.Includes.WithAttribute<WolverineMessageAttribute>();
        _messageQuery.Excludes.IsNotPublic();
    }

    internal IList<Assembly> Assemblies { get; } = new List<Assembly>();

    /// <summary>
    /// Customize the conventional filtering on the handler type discovery 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public HandlerDiscovery CustomizeHandlerDiscover(Action<TypeQuery> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure(_handlerQuery);
        return this;
    }
    
    /// <summary>
    /// Disables *all* conventional discovery of message handlers from type scanning. This is mostly useful for
    /// testing scenarios or folks who just really want to have full control over everything!
    /// </summary>
    public HandlerDiscovery DisableConventionalDiscovery(bool value = true)
    {
        _conventionalDiscoveryDisabled = value;
        return this;
    }

    internal IReadOnlyList<Type> FindAllMessages(HandlerGraph handlers)
    {
        return findAllMessages(handlers).Distinct().ToList();
    }

    internal IEnumerable<Type> findAllMessages(HandlerGraph handlers)
    {
        foreach (var chain in handlers.Chains)
        {
            yield return chain.MessageType;

            foreach (var publishedType in chain.PublishedTypes())
            {
                yield return publishedType;
            }
        }

        var discovered = _messageQuery.Find(Assemblies);
        foreach (var type in discovered)
        {
            yield return type;
        }
    }

    internal (Type, MethodInfo)[] FindCalls(WolverineOptions options)
    {
        if (_conventionalDiscoveryDisabled)
        {
            return _explicitTypes.SelectMany(actionsFromType).ToArray();
        }

        if (options.ApplicationAssembly != null)
        {
            Assemblies.Fill(options.ApplicationAssembly);
        }
        
        return _handlerQuery.Find(Assemblies)
            .Concat(_explicitTypes)
            .Distinct()
            .SelectMany(actionsFromType).ToArray();
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

    /// <summary>
    /// Customize how messages are discovered through type scanning. Note that any message
    /// type that is handled by this application or returned as a cascading message type
    /// will be discovered automatically
    /// </summary>
    /// <param name="customize"></param>
    /// <returns></returns>
    public HandlerDiscovery CustomizeMessageDiscovery(Action<TypeQuery> customize)
    {
        if (customize == null)
        {
            throw new ArgumentNullException(nameof(customize));
        }

        customize(_messageQuery);

        return this;
    }

}

