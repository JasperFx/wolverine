using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

/// <summary>
///     Base class for the pre-generated handler registry emitted by <c>codegen write</c>.
///     In <see cref="JasperFx.CodeGeneration.TypeLoadMode.Static" /> (or when a user opts in via
///     <see cref="WolverineOptions.UseStaticRegistries" />), Wolverine consumes the generated
///     subclass to skip the assembly scan that conventional handler discovery would otherwise
///     perform. See Wolverine#1577 (cold-start optimization).
/// </summary>
public abstract class HandlerRegistry
{
    /// <summary>
    ///     The C# identifier of the generated subclass. Lives under
    ///     <c>Internal.Generated.WolverineHandlers</c> after <c>codegen write</c>.
    /// </summary>
    public const string GeneratedTypeName = "GeneratedHandlerRegistry";

    /// <summary>
    ///     The concrete handler types discovered at <c>codegen write</c> time. Wolverine applies its
    ///     normal handler-method selection to exactly these types instead of scanning assemblies.
    /// </summary>
    public abstract Type[] HandlerTypes();

    /// <summary>
    ///     The conventional message types discovered at <c>codegen write</c> time — the result of
    ///     the <c>IMessage</c>/<c>[WolverineMessage]</c> assembly scan (see
    ///     <c>HandlerDiscovery.findAllMessages</c>). Captured so message-type discovery can also skip
    ///     the assembly scan in <see cref="TypeLoadMode.Static" />. Empty when the application declares
    ///     no such message types (handler-derived message types are still resolved from the handler graph).
    /// </summary>
    public abstract Type[] MessageTypes();
}

/// <summary>
///     <see cref="ICodeFile" /> that emits the <see cref="HandlerRegistry.GeneratedTypeName" /> subclass
///     of <see cref="HandlerRegistry" /> during <c>codegen write</c>, capturing the discovered handler
///     types as a compile-time <c>typeof(...)</c> array so no reflection-based assembly scan is needed
///     in <see cref="TypeLoadMode.Static" />.
/// </summary>
internal class HandlerRegistryCodeFile : ICodeFile
{
    private const string AotRootsTypeName = "AotRoots";

    private readonly Type[] _handlerTypes;
    private readonly Type[] _messageTypes;
    private readonly string[] _generatedHandlerTypeNames;
    private readonly Type[] _routedMessageTypes;
    private GeneratedType? _generatedType;

    public HandlerRegistryCodeFile(IEnumerable<Type> handlerTypes, IEnumerable<Type> messageTypes,
        IEnumerable<string>? generatedHandlerTypeNames = null, IEnumerable<Type>? routedMessageTypes = null)
    {
        _handlerTypes = onlyPublic(handlerTypes);
        _messageTypes = onlyPublic(messageTypes);

        // GH-4426. Ordered, like the arrays above: the emitted rooting block is part of the generated
        // output, and that output is byte-compared by the codegen drift gate (GH-4421).
        _generatedHandlerTypeNames = (generatedHandlerTypeNames ?? [])
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        // Both message-type sources, public-filtered for the same reason the arrays above are: a type
        // the generated file cannot see cannot appear inside a typeof().
        _routedMessageTypes = onlyPublic((routedMessageTypes ?? []).Concat(_messageTypes));
    }

    private static Type[] onlyPublic(IEnumerable<Type> types)
    {
        return types
            .Where(x => x is { IsPublic: true } or { IsNestedPublic: true })
            .Distinct()
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    public Type? RegistryType { get; private set; }

    string ICodeFile.FileName => HandlerRegistry.GeneratedTypeName;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        _generatedType = assembly.AddType(HandlerRegistry.GeneratedTypeName, typeof(HandlerRegistry));

        foreach (var type in _handlerTypes.Concat(_messageTypes).Concat(_routedMessageTypes))
        {
            assembly.ReferenceAssembly(type.Assembly);
        }

        _generatedType.MethodFor(nameof(HandlerRegistry.HandlerTypes))
            .Frames.Add(new WriteTypeArrayFrame(_handlerTypes));

        _generatedType.MethodFor(nameof(HandlerRegistry.MessageTypes))
            .Frames.Add(new WriteTypeArrayFrame(_messageTypes));

        // GH-4426 / jasperfx#743. Native AOT rooting, emitted rather than hand-written. Everything the
        // Static type loader reaches, it reaches REFLECTIVELY -- an assembly scan plus
        // Activator.CreateInstance -- so ILC has no static reference to any of it, trims it, and
        // TypeLoadMode.Static silently degrades to a scan that finds nothing. GH-4287 special-cased the
        // framework's own message types; every user-defined one still needed the app author to hand-write
        // this block (see AotRoots.Pin() in Wolverine.AotSmoke.Publish, which this replaces).
        //
        // Guarded by the same WithinCodegenCommand check as the message-type scan in explodeAllFiles:
        // during Static *attach* the committed file already carries the companion, and adding another
        // generated type to the in-memory assembly then would only confuse the loader.
        //
        // AddAotRoots is a no-op for `--language fsharp` (the F# compiler does not honour
        // ModuleInitializerAttribute), so there is no language branching to do here.
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            assembly.AddAotRoots(AotRootsTypeName, buildAotRoots(assembly.Namespace));
        }
    }

    /// <summary>
    ///     One <c>[DynamicDependency]</c> argument per type that the static type loader or the routing
    ///     warm-up reaches reflectively, and would therefore lose to the trimmer.
    /// </summary>
    private IEnumerable<AttributeArg> buildAotRoots(string generatedNamespace)
    {
        // The registry and every generated handler are SIBLINGS in the assembly being emitted: they have
        // no runtime Type while codegen is running, so they can only be named in code.
        yield return AttributeArg.TypeNamed($"{generatedNamespace}.{HandlerRegistry.GeneratedTypeName}");

        foreach (var typeName in _generatedHandlerTypeNames)
        {
            yield return AttributeArg.TypeNamed($"{generatedNamespace}.{typeName}");
        }

        // The handler classes. The generated code calls them directly, but handler-method selection walks
        // them with GetMethods(), and that needs metadata a direct call does not preserve.
        foreach (var handlerType in _handlerTypes)
        {
            yield return AttributeArg.Type(handlerType);
        }

        foreach (var messageType in _routedMessageTypes)
        {
            yield return AttributeArg.Type(messageType);

            // WolverineRuntime.PrepopulateRoutingCache closes these per message type reflectively, which
            // is exactly what threw MissingMethodException on startup in a native image for any message
            // type GH-4287 did not special-case. Neither router declares a generic constraint, so both
            // close over any message type at all.
            yield return closedRouterRoot(typeof(Routing.MessageRouter<>), messageType);
            yield return closedRouterRoot(typeof(Routing.EmptyMessageRouter<>), messageType);
        }
    }

    /// <summary>
    ///     Closes one of the open router generics over a message type so it can be named inside a
    ///     <c>[DynamicDependency]</c>.
    /// </summary>
    /// <remarks>
    ///     Deliberately its own method rather than an attribute on <see cref="buildAotRoots" />: that one
    ///     is an iterator, so its body compiles into a generated MoveNext and a suppression on the
    ///     declaring method does not reach it.
    /// </remarks>
    [UnconditionalSuppressMessage("AotAnalysis", "IL3050",
        Justification =
            "Only reached from `codegen write`, which runs on CoreCLR behind DynamicCodeBuilder.WithinCodegenCommand and never in a native image. Emitting these roots is exactly what removes the need for a native image to close these generics at runtime.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055",
        Justification =
            "The open generic is always MessageRouter<> or EmptyMessageRouter<>, neither of which declares a generic constraint, so there are no requirements for the trimmer to guarantee. The closed type is only ever named inside an emitted [DynamicDependency] -- it is never instantiated here.")]
    private static AttributeArg closedRouterRoot(Type openRouterType, Type messageType)
    {
        return AttributeArg.Type(openRouterType.MakeGenericType(messageType));
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        var found = this.As<ICodeFile>().AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over the generated assembly to attach the generated registry type; the type is known by construction at codegen time. See AOT guide.")]
    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        RegistryType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == HandlerRegistry.GeneratedTypeName);
        return RegistryType != null;
    }
}

/// <summary>
///     Writes the body of a <see cref="HandlerRegistry" /> type-array accessor
///     (<see cref="HandlerRegistry.HandlerTypes" /> / <see cref="HandlerRegistry.MessageTypes" />) as a
///     single <c>typeof(...)</c> array literal — fully resolved at codegen time, no reflection at runtime.
/// </summary>
internal class WriteTypeArrayFrame : SyncFrame
{
    private readonly Type[] _types;

    public WriteTypeArrayFrame(Type[] types)
    {
        _types = types;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_types.Length == 0)
        {
            writer.Write("return System.Array.Empty<System.Type>();");
        }
        else
        {
            var literals = string.Join(", ", _types.Select(t => $"typeof({t.FullNameInCode()})"));
            writer.Write($"return new System.Type[] {{ {literals} }};");
        }

        Next?.GenerateCode(method, writer);
    }

    // F# counterpart so `codegen write --language fsharp` can emit the static HandlerRegistry.
    //
    // F# modules compile to sealed abstract classes but F# syntax forbids using them as typeof<>
    // type arguments (SourceConstructFlags.Module = 7).  For those we fall back to the runtime
    // Type.GetType path.  Plain F# classes / C# types use the compile-time typeof<T> form, which
    // the CodegenWriteFSharpCli tests assert is present for class-based handlers.
    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_types.Length == 0)
        {
            writer.Write("System.Array.Empty<System.Type>()");
        }
        else
        {
            var parts = _types.Select(t => IsFSharpModule(t)
                ? $"(System.Type.GetType(\"{t.AssemblyQualifiedName}\") |> Option.ofObj)"
                : $"(Some typeof<{t.FullNameInCode()}>)");
            writer.Write($"[| {string.Join("; ", parts)} |] |> Array.choose id");
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    private static bool IsFSharpModule(Type t)
    {
        // F# modules compile to abstract sealed classes (the .NET "static class" pattern).
        // F# classes/records are concrete (not abstract+sealed), so this check reliably
        // separates modules (which cannot appear as typeof<> type arguments in F# syntax)
        // from classes that can.  C# static classes are also abstract+sealed but handler
        // types are concrete instances, so there is no false-positive concern in practice.
        return t.IsAbstract && t.IsSealed;
    }
}
