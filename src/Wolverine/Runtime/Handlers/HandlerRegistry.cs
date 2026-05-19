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
    private GeneratedType? _generatedType;

    public HandlerRegistryCodeFile(IEnumerable<Type> handlerTypes)
    {
        _handlerTypes = handlerTypes
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

        foreach (var type in _handlerTypes)
        {
            assembly.ReferenceAssembly(type.Assembly);
        }

        var method = _generatedType.MethodFor(nameof(HandlerRegistry.HandlerTypes));
        method.Frames.Add(new WriteHandlerTypesFrame(_handlerTypes));
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
///     Writes the body of <see cref="HandlerRegistry.HandlerTypes" /> as a single
///     <c>typeof(...)</c> array literal — fully resolved at codegen time, no reflection at runtime.
/// </summary>
internal class WriteHandlerTypesFrame : SyncFrame
{
    private readonly Type[] _types;

    public WriteHandlerTypesFrame(Type[] types)
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
}
