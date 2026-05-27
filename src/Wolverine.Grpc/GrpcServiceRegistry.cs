using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Grpc;

/// <summary>
///     Base class for the pre-generated gRPC service registry emitted by <c>codegen write</c>
///     (GH-2926, the Wolverine.Grpc counterpart to the handler manifest in GH-2906). In
///     <see cref="TypeLoadMode.Static" />, Wolverine.Grpc consumes the generated subclass to skip the
///     <c>GetExportedTypes</c> assembly scans that <see cref="GrpcGraph.DiscoverServices" /> would
///     otherwise perform across the three discovery flavors (proto-first, code-first, hand-written).
/// </summary>
public abstract class GrpcServiceRegistry
{
    /// <summary>
    ///     The C# identifier of the generated subclass. Lives under
    ///     <c>Internal.Generated.WolverineHandlers</c> after <c>codegen write</c>.
    /// </summary>
    public const string GeneratedTypeName = "GeneratedGrpcServiceRegistry";

    /// <summary>Proto-first stub types discovered at <c>codegen write</c> time.</summary>
    public abstract Type[] ProtoFirstStubTypes();

    /// <summary>Code-first <c>[ServiceContract]</c> interface types discovered at <c>codegen write</c> time.</summary>
    public abstract Type[] CodeFirstContractTypes();

    /// <summary>Hand-written service class types discovered at <c>codegen write</c> time.</summary>
    public abstract Type[] HandWrittenServiceTypes();

    /// <summary>
    ///     Direct-mapped hand-written service class types discovered at <c>codegen write</c> time — the
    ///     concrete <c>*GrpcService</c>/<c>[WolverineGrpcService]</c> classes that receive no generated
    ///     wrapper and are mapped directly by <c>MapWolverineGrpcServices()</c> (see
    ///     <c>WolverineGrpcExtensions.FindGrpcServiceTypes</c>). Captured so that map-time discovery can
    ///     skip its assembly scan too.
    /// </summary>
    public abstract Type[] DirectMappedServiceTypes();

    // Locates the pre-generated GrpcServiceRegistry subclass in the application assembly. The
    // single-assembly ExportedTypes walk is far cheaper than service discovery's multi-assembly scans.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over the application assembly to find the codegen-emitted GrpcServiceRegistry; the type is rooted by construction. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification =
            "Activator.CreateInstance over the generated GrpcServiceRegistry subclass; the type carries its codegen-emitted public parameterless constructor. See AOT guide.")]
    internal static bool TryLoad(Assembly? applicationAssembly, out GrpcServiceRegistry? registry)
    {
        registry = null;

        if (applicationAssembly == null)
        {
            return false;
        }

        var registryType = applicationAssembly.ExportedTypes.FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false } && typeof(GrpcServiceRegistry).IsAssignableFrom(t));

        if (registryType == null)
        {
            return false;
        }

        registry = (GrpcServiceRegistry)Activator.CreateInstance(registryType)!;
        return true;
    }
}

/// <summary>
///     <see cref="ICodeFile" /> that emits the <see cref="GrpcServiceRegistry.GeneratedTypeName" />
///     subclass during <c>codegen write</c>, capturing the discovered service types (across all three
///     discovery flavors) as compile-time <c>typeof(...)</c> arrays so no reflection-based assembly scan
///     is needed in <see cref="TypeLoadMode.Static" />.
/// </summary>
internal class GrpcServiceRegistryCodeFile : ICodeFile
{
    private readonly Type[] _protoFirstStubTypes;
    private readonly Type[] _codeFirstContractTypes;
    private readonly Type[] _handWrittenServiceTypes;
    private readonly Type[] _directMappedServiceTypes;
    private GeneratedType? _generatedType;

    public GrpcServiceRegistryCodeFile(IEnumerable<Type> protoFirstStubTypes,
        IEnumerable<Type> codeFirstContractTypes, IEnumerable<Type> handWrittenServiceTypes,
        IEnumerable<Type> directMappedServiceTypes)
    {
        _protoFirstStubTypes = onlyPublic(protoFirstStubTypes);
        _codeFirstContractTypes = onlyPublic(codeFirstContractTypes);
        _handWrittenServiceTypes = onlyPublic(handWrittenServiceTypes);
        _directMappedServiceTypes = onlyPublic(directMappedServiceTypes);
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

    string ICodeFile.FileName => GrpcServiceRegistry.GeneratedTypeName;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        _generatedType = assembly.AddType(GrpcServiceRegistry.GeneratedTypeName, typeof(GrpcServiceRegistry));

        foreach (var type in _protoFirstStubTypes.Concat(_codeFirstContractTypes)
                     .Concat(_handWrittenServiceTypes).Concat(_directMappedServiceTypes))
        {
            assembly.ReferenceAssembly(type.Assembly);
        }

        _generatedType.MethodFor(nameof(GrpcServiceRegistry.ProtoFirstStubTypes))
            .Frames.Add(new WriteServiceTypesFrame(_protoFirstStubTypes));

        _generatedType.MethodFor(nameof(GrpcServiceRegistry.CodeFirstContractTypes))
            .Frames.Add(new WriteServiceTypesFrame(_codeFirstContractTypes));

        _generatedType.MethodFor(nameof(GrpcServiceRegistry.HandWrittenServiceTypes))
            .Frames.Add(new WriteServiceTypesFrame(_handWrittenServiceTypes));

        _generatedType.MethodFor(nameof(GrpcServiceRegistry.DirectMappedServiceTypes))
            .Frames.Add(new WriteServiceTypesFrame(_directMappedServiceTypes));
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
        RegistryType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == GrpcServiceRegistry.GeneratedTypeName);
        return RegistryType != null;
    }
}

/// <summary>
///     Writes the body of a <see cref="GrpcServiceRegistry" /> type-array accessor as a single
///     <c>typeof(...)</c> array literal — fully resolved at codegen time, no reflection at runtime.
/// </summary>
internal class WriteServiceTypesFrame : SyncFrame
{
    private readonly Type[] _types;

    public WriteServiceTypesFrame(Type[] types)
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
