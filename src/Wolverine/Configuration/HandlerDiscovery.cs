using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Core;
using JasperFx.Core.TypeScanning;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
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

    // Set (only) by TryLoadStaticHandlerRegistry from the generated manifest's MessageTypes(); when
    // non-null, findAllMessages uses it instead of scanning assemblies. See GH-2906.
    private Type[]? _staticMessageTypes;

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
    /// 
    /// </summary>
    public bool IncludeHandlerModules { get; set; } = false;
    
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

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Conventional handler discovery walks Assembly.ExportedTypes via JasperFx TypeQuery. Trimmed-away handler types simply do not register; AOT consumers should opt out of conventional discovery (DisableConventionalDiscovery) or use IncludeType to make registration explicit.")]
    internal IEnumerable<Type> findAllMessages(HandlerGraph handlers)
    {
        foreach (var messageType in handlers.AllMessageTypes())
        {
            if (messageType == typeof(object)) continue;
            if (messageType == typeof(object[])) continue;
            if (messageType == typeof(IEnumerable<object>)) continue;
            
            yield return messageType;
        }

        // In TypeLoadMode.Static the conventional message types were captured into the generated
        // HandlerRegistry at codegen write time (cached by TryLoadStaticHandlerRegistry), so we skip
        // the IMessage/[WolverineMessage] assembly scan entirely. Falls back to the scan otherwise.
        var discovered = _staticMessageTypes ?? _messageQuery.Find(Assemblies);
        foreach (var type in discovered) yield return type;
    }

    // The conventional message types (IMessage implementations + [WolverineMessage]) found by scanning
    // the discovery assemblies. Used only at `codegen write` time to capture them into the generated
    // HandlerRegistry; at runtime under TypeLoadMode.Static they are read back from the manifest instead.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Conventional message-type scan executed at `codegen write` time to populate the static manifest; runtime TypeLoadMode.Static reads the captured typeof(...) literals and never calls this. See AOT guide.")]
    internal Type[] DiscoverConventionalMessageTypes()
    {
        return _messageQuery.Find(Assemblies).ToArray();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "HandlerQuery.Find walks Assembly.ExportedTypes via JasperFx TypeQuery. AOT consumers either DisableConventionalDiscovery() and register explicit types via IncludeType, or rely on the AOT publishing guide's source-generated handler manifest.")]
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

    // Cold-start fast path (Wolverine#1577 Tier 1): build the same (Type, MethodInfo)
    // pairs FindCalls would produce, but from a known, pre-discovered set of handler
    // types (the generated HandlerRegistry) instead of an assembly scan. Reuses the
    // exact actionsFromType selection so behavior is identical to a full scan.
    internal (Type, MethodInfo)[] FindCallsFromTypes(IReadOnlyList<Type> handlerTypes, WolverineOptions options)
    {
        if (options.ApplicationAssembly != null)
        {
            Assemblies.Fill(options.ApplicationAssembly);
        }

        return handlerTypes
            .Concat(_explicitTypes)
            .Distinct()
            .SelectMany(actionsFromType)
            .ToArray();
    }

    // Locates the pre-generated HandlerRegistry subclass in the application assembly and
    // returns its captured handler types. The single-assembly ExportedTypes walk is far
    // cheaper than conventional discovery's multi-assembly scan + convention filtering.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over the application assembly to find the codegen-emitted HandlerRegistry; the type is rooted by construction. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification =
            "Activator.CreateInstance over the generated HandlerRegistry subclass; the type carries its codegen-emitted public parameterless constructor. See AOT guide.")]
    internal bool TryLoadStaticHandlerRegistry(WolverineOptions options, out IReadOnlyList<Type> handlerTypes)
    {
        handlerTypes = Array.Empty<Type>();

        var assembly = options.ApplicationAssembly;
        if (assembly == null)
        {
            return false;
        }

        var registryType = assembly.ExportedTypes.FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false } && typeof(HandlerRegistry).IsAssignableFrom(t));

        if (registryType == null)
        {
            return false;
        }

        var registry = (HandlerRegistry)Activator.CreateInstance(registryType)!;
        handlerTypes = registry.HandlerTypes();

        // Cache the conventional message types captured in the manifest so findAllMessages can skip its
        // own IMessage/[WolverineMessage] assembly scan (GH-2906). Only set on this Static-mode path,
        // so a null _staticMessageTypes means "fall back to scanning".
        _staticMessageTypes = registry.MessageTypes();
        return true;
    }

    internal IList<Type> ExplicitTypes => _explicitTypes;

    /// <summary>
    /// Discovers and includes all assemblies marked with [WolverineHandlerModule] attribute.
    /// </summary>
    internal HandlerDiscovery DiscoverHandlerModules(Assembly? applicationAssembly)
    {
        // GH-2905: load the deployed module assemblies via the runtime's deployment list (shared with
        // extension discovery, GH-2902) instead of the AssemblyFinder.FindAssemblies bin-directory probe,
        // then pick out the [WolverineHandlerModule]-marked ones from the loaded set. No filesystem scan.
        ModuleAssemblyLoader.EnsureDeployedModuleAssembliesAreLoaded(applicationAssembly);

        var handlerModuleAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.HasAttribute<WolverineHandlerModuleAttribute>())
            .Distinct()
            .ToArray();

        Assemblies.AddRange(handlerModuleAssemblies);
        return this;
    }

    // GetMethods on a runtime-resolved handler type from assembly scanning.
    // Same chunk D / chunk G pattern: handler types come from
    // Assembly.ExportedTypes walking, which is fundamentally not AOT-clean
    // (the trimmer can't follow assembly scans). AOT-clean apps use
    // TypeLoadMode.Static where the handler graph is pre-discovered into
    // source-generated registration; this method is bypassed entirely.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Handler-type method walk from assembly scanning; AOT-clean apps run TypeLoadMode.Static with pre-generated handler registration. See AOT guide.")]
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