using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Core;
using JasperFx.Core.TypeScanning;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

public sealed partial class HandlerDiscovery
{
    private readonly IList<Type> _explicitTypes = new List<Type>();

    private readonly TypeQuery _messageQuery = new(TypeClassification.Concretes| TypeClassification.Closed);

    private readonly string[] _validMethods =
    [
        HandlerChain.Handle, HandlerChain.Handles, HandlerChain.Consume, HandlerChain.Consumes, SagaChain.Orchestrate,
        SagaChain.Orchestrates, SagaChain.Start, SagaChain.Starts, SagaChain.StartOrHandle, SagaChain.StartsOrHandles,
        SagaChain.NotFound
    ];

    private bool _conventionalDiscoveryDisabled;

    public HandlerDiscovery()
    {
        specifyHandlerMethodRules();
        
        _messageQuery.Excludes.IsStatic();
        _messageQuery.Excludes.WithCondition(
            $"Not implements {typeof(IMessage).FullNameInCode()} nor has attribute {typeof(WolverineMessageAttribute).FullNameInCode()}",
            x => !x.CanBeCastTo<IMessage>() && !x.HasAttribute<WolverineMessageAttribute>());
        _messageQuery.Includes.Implements<IMessage>();
        _messageQuery.Includes.WithAttribute<WolverineMessageAttribute>();
        _messageQuery.Excludes.IsNotPublic();
        _messageQuery.Includes.WithCondition("Is concrete", x => x.IsConcrete());
    }

    internal JasperFx.Core.Filters.CompositeFilter<MethodInfo> MethodIncludes { get; } = new();

    internal JasperFx.Core.Filters.CompositeFilter<MethodInfo> MethodExcludes { get; } = new();

    internal TypeQuery HandlerQuery { get; } = new(TypeClassification.Concretes | TypeClassification.Closed);

    internal IList<Assembly> Assemblies { get; } = new List<Assembly>();

    private void specifyHandlerMethodRules()
    {
        foreach (var methodName in _validMethods)
        {
            MethodIncludes.WithCondition($"Method name is '{methodName}' (case sensitive)", m => m.Name == methodName);

            var asyncName = methodName + "Async";
            MethodIncludes.WithCondition($"Method name is '{asyncName}' (case sensitive)", m => m.Name == asyncName);
        }

        MethodIncludes.WithCondition("Has attribute [WolverineHandler]",
            m => m.HasAttribute<WolverineHandlerAttribute>());

        MethodExcludes.WithCondition("Method is declared by object", method => method.DeclaringType == typeof(object));
        MethodExcludes.WithCondition("IDisposable.Dispose()", method => method.Name == nameof(IDisposable.Dispose));
        MethodExcludes.WithCondition("IAsyncDisposable.DisposeAsync()",
            method => method.Name == nameof(IAsyncDisposable.DisposeAsync));
        MethodExcludes.WithCondition("Contains Generic Parameters", method => method.ContainsGenericParameters);
        MethodExcludes.WithCondition("Special Name", method => method.IsSpecialName);
        MethodExcludes.WithCondition("Has attribute [WolverineIgnore]",
            method => method.HasAttribute<WolverineIgnoreAttribute>());


        MethodExcludes.WithCondition("Has no arguments", m => m.GetParameters().Length == 0);

        MethodExcludes.WithCondition("Cannot determine a valid message type", m => m.MessageType() == null);

        MethodExcludes.WithCondition("Returns a primitive type",
            m => m.ReturnType != typeof(void) && m.ReturnType.IsPrimitive);
    }

    private void specifyConventionalHandlerDiscovery()
    {
        HandlerQuery.Excludes.WithCondition("Not GeneratedStreamStateQueryHandler", t => t.Name == "GeneratedStreamStateQueryHandler");
        HandlerQuery.Includes.WithNameSuffix(HandlerChain.HandlerSuffix);
        HandlerQuery.Includes.WithNameSuffix(HandlerChain.ConsumerSuffix);
        HandlerQuery.Includes.Implements<Saga>();
        HandlerQuery.Includes.Implements<IWolverineHandler>();
        HandlerQuery.Includes.WithAttribute<WolverineHandlerAttribute>();

        HandlerQuery.Excludes.WithCondition("Is not a public type", t => isNotPublicType(t));
        HandlerQuery.Excludes.WithAttribute<WolverineIgnoreAttribute>();
    }

    private static bool isNotPublicType(Type type)
    {
        if (type.IsPublic)
        {
            return false;
        }

        if (type.IsNestedPublic)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///  Customize the conventional filtering on the handler type discovery. This is *additive* to the
    /// built in conventional handler discovery. Disabling conventional discovery will negate anything
    /// done with this method
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public HandlerDiscovery CustomizeHandlerDiscovery(Action<TypeQuery> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure(HandlerQuery);
        return this;
    }

    /// <summary>
    /// Customize rules for what Wolverine does or does not consider a valid message type
    /// Advanced usage!
    /// </summary>
    public TypeQuery MessageQuery => _messageQuery;

    /// <summary>
    ///     Disables *all* conventional discovery of message handlers from type scanning. This is mostly useful for
    ///     testing scenarios or folks who just really want to have full control over everything!
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
        foreach (var messageType in handlers.AllMessageTypes())
        {
            yield return messageType;
        }

        var discovered = _messageQuery.Find(Assemblies);
        foreach (var type in discovered) yield return type;
    }

    internal (Type, MethodInfo)[] FindCalls(WolverineOptions options)
    {
        if (options.ApplicationAssembly != null)
        {
            Assemblies.Fill(options.ApplicationAssembly);
        }
        
        if (_conventionalDiscoveryDisabled)
        {
            return HandlerQuery.Find(Assemblies)
                .Concat(_explicitTypes)
                .Distinct()
                .SelectMany(actionsFromType).ToArray();
        }
        
        specifyConventionalHandlerDiscovery();

        return HandlerQuery.Find(Assemblies)
            .Concat(_explicitTypes)
            .Distinct()
            .SelectMany(actionsFromType).ToArray();
    }

    private IEnumerable<(Type, MethodInfo)> actionsFromType(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.DeclaringType != typeof(object)).ToArray()
            .Where(m => MethodIncludes.Matches(m) && !MethodExcludes.Matches(m))
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
    ///     Customize how messages are discovered through type scanning. Note that any message
    ///     type that is handled by this application or returned as a cascading message type
    ///     will be discovered automatically
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

    public void IgnoreAssembly(Assembly assembly)
    {
        Assemblies.Remove(assembly);
    }
}