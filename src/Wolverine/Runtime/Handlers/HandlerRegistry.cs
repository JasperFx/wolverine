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
    private readonly Type[] _handlerTypes;
    private readonly Type[] _messageTypes;
    private GeneratedType? _generatedType;

    public HandlerRegistryCodeFile(IEnumerable<Type> handlerTypes, IEnumerable<Type> messageTypes)
    {
        _handlerTypes = onlyPublic(handlerTypes);
        _messageTypes = onlyPublic(messageTypes);
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

        foreach (var type in _handlerTypes.Concat(_messageTypes))
        {
            assembly.ReferenceAssembly(type.Assembly);
        }

        _generatedType.MethodFor(nameof(HandlerRegistry.HandlerTypes))
            .Frames.Add(new WriteTypeArrayFrame(_handlerTypes));

        _generatedType.MethodFor(nameof(HandlerRegistry.MessageTypes))
            .Frames.Add(new WriteTypeArrayFrame(_messageTypes));
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

    // F# counterpart so `codegen write --language fsharp` can emit the static HandlerRegistry. F#
    // method bodies are expressions (no `return`/`;`): an empty array, or an F# array literal of
    // typeof<...> values.
    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_types.Length == 0)
        {
            writer.Write("System.Array.Empty<System.Type>()");
        }
        else
        {
            var literals = string.Join("; ", _types.Select(t => $"typeof<{t.FSharpName()}>"));
            writer.Write($"[| {literals} |]");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
