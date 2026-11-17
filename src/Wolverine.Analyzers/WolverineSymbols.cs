using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Wolverine.Analyzers;

/// <summary>
/// The Wolverine types the analyzers look for, resolved once per compilation. Null when the compilation does not
/// reference Wolverine, in which case none of the rules run.
/// </summary>
internal sealed class WolverineSymbols
{
    private static readonly ImmutableHashSet<string> HandlerMethodNames = ImmutableHashSet.Create(
        "Handle", "Handles", "Consume", "Consumes");

    // Saga method names Wolverine also discovers on any handler type. Only checked on Saga types, where they
    // can't be mistaken for an ordinary helper method
    private static readonly ImmutableHashSet<string> SagaMethodNames = ImmutableHashSet.Create(
        "Orchestrate", "Orchestrates", "Start", "Starts", "StartOrHandle", "StartsOrHandles", "NotFound");

    private WolverineSymbols(Compilation compilation, INamedTypeSymbol messageBus)
    {
        MessageBus = messageBus;
        Saga = compilation.GetTypeByMetadataName("Wolverine.Saga");
        WolverineHandlerInterface = compilation.GetTypeByMetadataName("Wolverine.IWolverineHandler");
        WolverineHandlerAttribute = compilation.GetTypeByMetadataName("Wolverine.Attributes.WolverineHandlerAttribute");
        WolverineIgnoreAttribute = compilation.GetTypeByMetadataName("Wolverine.Attributes.WolverineIgnoreAttribute");
        JasperFxIgnoreAttribute = compilation.GetTypeByMetadataName("JasperFx.Core.JasperFxIgnoreAttribute");
        MessageHandler = compilation.GetTypeByMetadataName("Wolverine.Runtime.Handlers.MessageHandler");
        SagaIdentityFromAttribute =
            compilation.GetTypeByMetadataName("Wolverine.Persistence.Sagas.SagaIdentityFromAttribute");
        HttpMethodAttribute = compilation.GetTypeByMetadataName("Wolverine.Http.WolverineHttpMethodAttribute");
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        TaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        ValueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        ValueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
    }

    public INamedTypeSymbol MessageBus { get; }
    public INamedTypeSymbol? Saga { get; }
    public INamedTypeSymbol? WolverineHandlerInterface { get; }
    public INamedTypeSymbol? WolverineHandlerAttribute { get; }
    public INamedTypeSymbol? WolverineIgnoreAttribute { get; }
    public INamedTypeSymbol? JasperFxIgnoreAttribute { get; }
    public INamedTypeSymbol? MessageHandler { get; }
    public INamedTypeSymbol? SagaIdentityFromAttribute { get; }
    public INamedTypeSymbol? HttpMethodAttribute { get; }
    public INamedTypeSymbol? Task { get; }
    public INamedTypeSymbol? TaskOfT { get; }
    public INamedTypeSymbol? ValueTask { get; }
    public INamedTypeSymbol? ValueTaskOfT { get; }

    public static WolverineSymbols? TryCreate(Compilation compilation)
    {
        var messageBus = compilation.GetTypeByMetadataName("Wolverine.IMessageBus");
        return messageBus == null ? null : new WolverineSymbols(compilation, messageBus);
    }

    /// <summary>
    /// Mirrors the conventional handler type rules in Wolverine's HandlerDiscovery: a class named *Handler or
    /// *Consumer, a Saga, an IWolverineHandler or a [WolverineHandler] class, that is concrete (or static), closed,
    /// and not [WolverineIgnore]. Accessibility is deliberately NOT checked here.
    /// </summary>
    public bool IsConventionalHandlerType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsImplicitlyDeclared) return false;
        if (type.IsAbstract && !type.IsStatic) return false;
        if (isGenericOrInsideGeneric(type)) return false;
        if (type.Name == "GeneratedStreamStateQueryHandler") return false;
        if (HasIgnoreAttribute(type)) return false;
        if (MessageHandler != null && inheritsFrom(type, MessageHandler)) return false;

        if (type.Name.EndsWith("Handler", System.StringComparison.Ordinal)) return true;
        if (type.Name.EndsWith("Consumer", System.StringComparison.Ordinal)) return true;
        if (IsSaga(type)) return true;
        if (WolverineHandlerInterface != null &&
            type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, WolverineHandlerInterface)))
        {
            return true;
        }

        return hasAttribute(type, WolverineHandlerAttribute);
    }

    public bool IsSaga(INamedTypeSymbol type) => Saga != null && inheritsFrom(type, Saga);

    /// <summary>
    /// A method Wolverine would take as a handler method on a conventional handler type, whatever its
    /// accessibility: an ordinary, non-generic method with at least one parameter, named by convention or marked
    /// [WolverineHandler], not [WolverineIgnore], and not returning a primitive.
    /// </summary>
    public bool IsHandlerMethodCandidate(IMethodSymbol method, bool isSaga)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsImplicitlyDeclared) return false;
        if (method.IsGenericMethod || method.Parameters.Length == 0) return false;
        if (HasIgnoreAttribute(method)) return false;
        if (!method.ReturnsVoid && isPrimitive(method.ReturnType)) return false;

        if (hasAttribute(method, WolverineHandlerAttribute)) return true;

        var name = method.Name.EndsWith("Async", System.StringComparison.Ordinal)
            ? method.Name.Substring(0, method.Name.Length - "Async".Length)
            : method.Name;

        return HandlerMethodNames.Contains(name) || (isSaga && SagaMethodNames.Contains(name));
    }

    public bool IsHttpEndpoint(IMethodSymbol method) => HttpMethodAttribute != null && hasAttribute(method, HttpMethodAttribute);

    public bool HasWolverineHandlerAttribute(ISymbol symbol) => hasAttribute(symbol, WolverineHandlerAttribute);

    public bool HasIgnoreAttribute(ISymbol symbol) =>
        hasAttribute(symbol, WolverineIgnoreAttribute) || hasAttribute(symbol, JasperFxIgnoreAttribute);

    public bool IsTaskLike(ITypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return matches(definition, Task) || matches(definition, TaskOfT) || matches(definition, ValueTask) ||
               matches(definition, ValueTaskOfT);
    }

    /// <summary>
    /// Task&lt;T&gt; or ValueTask&lt;T&gt; unwrapped to T, anything else as is
    /// </summary>
    public ITypeSymbol UnwrapAsync(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named &&
            (matches(named.OriginalDefinition, TaskOfT) || matches(named.OriginalDefinition, ValueTaskOfT)))
        {
            return named.TypeArguments[0];
        }

        return type;
    }

    private static bool matches(ITypeSymbol type, INamedTypeSymbol? expected) =>
        expected != null && SymbolEqualityComparer.Default.Equals(type, expected);

    private static bool hasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
    {
        if (attributeType == null) return false;

        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass != null && inheritsFrom(attribute.AttributeClass, attributeType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool inheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType)) return true;
        }

        return false;
    }

    private static bool isGenericOrInsideGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.ContainingType)
        {
            if (current.IsGenericType) return true;
        }

        return false;
    }

    // System.Type.IsPrimitive: decimal and string are not primitives
    private static bool isPrimitive(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return true;
            default:
                return false;
        }
    }
}
