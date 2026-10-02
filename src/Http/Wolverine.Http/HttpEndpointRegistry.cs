using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Http;

/// <summary>
///     Base class for the pre-generated HTTP endpoint registry emitted by <c>codegen write</c>
///     (GH-2925, the Wolverine.Http counterpart to the handler manifest in GH-2906). In
///     <see cref="TypeLoadMode.Static" />, Wolverine.Http consumes the generated subclass to skip the
///     <see cref="HttpChainSource.FindActions()" /> assembly scan that endpoint discovery would
///     otherwise perform.
/// </summary>
public abstract class HttpEndpointRegistry
{
    /// <summary>
    ///     The C# identifier of the generated subclass. Lives under
    ///     <c>Internal.Generated.WolverineHandlers</c> after <c>codegen write</c>.
    /// </summary>
    public const string GeneratedTypeName = "GeneratedHttpEndpointRegistry";

    /// <summary>
    ///     The endpoint-declaring types discovered at <c>codegen write</c> time. Wolverine.Http applies
    ///     its normal endpoint-method selection to exactly these types instead of scanning assemblies.
    /// </summary>
    public abstract Type[] EndpointTypes();

    // Locates the pre-generated HttpEndpointRegistry subclass in the application assembly and returns
    // its captured endpoint types. The single-assembly ExportedTypes walk is far cheaper than endpoint
    // discovery's multi-assembly scan + convention filtering.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "ExportedTypes walk over the application assembly to find the codegen-emitted HttpEndpointRegistry; the type is rooted by construction. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification =
            "Activator.CreateInstance over the generated HttpEndpointRegistry subclass; the type carries its codegen-emitted public parameterless constructor. See AOT guide.")]
    internal static bool TryLoad(Assembly? applicationAssembly, out IReadOnlyList<Type> endpointTypes)
    {
        endpointTypes = Array.Empty<Type>();

        if (applicationAssembly == null)
        {
            return false;
        }

        var registryType = applicationAssembly.ExportedTypes.FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false } && typeof(HttpEndpointRegistry).IsAssignableFrom(t));

        if (registryType == null)
        {
            return false;
        }

        var registry = (HttpEndpointRegistry)Activator.CreateInstance(registryType)!;
        endpointTypes = registry.EndpointTypes();
        return true;
    }
}

/// <summary>
///     <see cref="ICodeFile" /> that emits the <see cref="HttpEndpointRegistry.GeneratedTypeName" />
///     subclass of <see cref="HttpEndpointRegistry" /> during <c>codegen write</c>, capturing the
///     discovered endpoint types as a compile-time <c>typeof(...)</c> array so no reflection-based
///     assembly scan is needed in <see cref="TypeLoadMode.Static" />.
/// </summary>
internal class HttpEndpointRegistryCodeFile : ICodeFile
{
    // GH-4778: deliberately NOT "AotRoots". The handler registry emits a type by that name into the same
    // Internal.Generated.WolverineHandlers namespace, and an application with both committed would not
    // compile.
    private const string AotRootsTypeName = "HttpAotRoots";

    private readonly Type[] _endpointTypes;
    private readonly Type[] _responseAwareTypes;
    private readonly string[] _generatedEndpointTypeNames;
    private GeneratedType? _generatedType;

    public HttpEndpointRegistryCodeFile(IEnumerable<Type> endpointTypes,
        IEnumerable<Type>? responseAwareTypes = null, IEnumerable<string>? generatedEndpointTypeNames = null)
    {
        // Ordered for the same reason as everything else here: the emitted rooting block is part of the
        // generated output, which the codegen drift gate byte-compares.
        _generatedEndpointTypeNames = (generatedEndpointTypeNames ?? [])
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        _responseAwareTypes = (responseAwareTypes ?? [])
            .Where(x => x is { IsPublic: true } or { IsNestedPublic: true })
            .Distinct()
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToArray();

        _endpointTypes = endpointTypes
            .Where(x => x is { IsPublic: true } or { IsNestedPublic: true })
            .Distinct()
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    public Type? RegistryType { get; private set; }

    string ICodeFile.FileName => HttpEndpointRegistry.GeneratedTypeName;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        _generatedType = assembly.AddType(HttpEndpointRegistry.GeneratedTypeName, typeof(HttpEndpointRegistry));

        foreach (var type in _endpointTypes.Concat(_responseAwareTypes))
        {
            assembly.ReferenceAssembly(type.Assembly);
        }

        _generatedType.MethodFor(nameof(HttpEndpointRegistry.EndpointTypes))
            .Frames.Add(new WriteEndpointTypesFrame(_endpointTypes));

        // GH-4778. Until now the HTTP side emitted NO rooting block at all -- the handler registry has had
        // one since GH-4426, and this counterpart never got it. That is why the reported crash surfaced on
        // HTTP endpoints: an endpoint returning Marten's UpdatedAggregate makes HttpChain close
        // Applier<UpdatedAggregate> at startup, and nothing rooted the instantiation.
        //
        // Same WithinCodegenCommand guard as the handler registry: during Static *attach* the committed
        // file already carries the companion.
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            assembly.AddAotRoots(AotRootsTypeName, buildAotRoots(assembly.Namespace));
        }
    }

    /// <summary>
    ///     One <c>[DynamicDependency]</c> argument per type the HTTP side reaches reflectively at startup
    ///     and would otherwise lose to the trimmer.
    /// </summary>
    private IEnumerable<AttributeArg> buildAotRoots(string generatedNamespace)
    {
        // A sibling in the assembly being emitted: no runtime Type exists for it while codegen runs, so it
        // can only be named in code.
        yield return AttributeArg.TypeNamed($"{generatedNamespace}.{HttpEndpointRegistry.GeneratedTypeName}");

        // Likewise siblings: the generated endpoint types, which Static mode looks up BY NAME in
        // AssertPreBuiltTypesExist and then executes. Without these the native image throws
        // MissingPreBuiltTypesException before it ever gets as far as the Applier<T> close below.
        foreach (var typeName in _generatedEndpointTypeNames)
        {
            yield return AttributeArg.TypeNamed($"{generatedNamespace}.{typeName}");
        }

        // TryLoad walks ExportedTypes to find the registry and then Activator.CreateInstance's it, and
        // endpoint-method selection walks the endpoint types with GetMethods() -- metadata a direct call
        // does not preserve.
        foreach (var endpointType in _endpointTypes)
        {
            yield return AttributeArg.Type(endpointType);
        }

        foreach (var responseAwareType in _responseAwareTypes)
        {
            yield return AttributeArg.Type(responseAwareType);
            yield return closedApplierRoot(responseAwareType);
        }
    }

    /// <summary>
    ///     Closes <c>Applier&lt;&gt;</c> over one <see cref="IResponseAware" /> type so it can be named
    ///     inside a <c>[DynamicDependency]</c>. Its own method for the same reason as the handler registry's
    ///     equivalent: <see cref="buildAotRoots" /> is an iterator, so a suppression on the declaring method
    ///     would not reach the generated MoveNext.
    /// </summary>
    [UnconditionalSuppressMessage("AotAnalysis", "IL3050",
        Justification =
            "Only reached from `codegen write`, which runs on CoreCLR behind DynamicCodeBuilder.WithinCodegenCommand and never in a native image. Emitting this root is exactly what removes the need for a native image to close the generic at runtime.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055",
        Justification =
            "Applier<T> constrains T to IResponseAware, and the argument comes from HttpChain.ReturnVariablesOfType(typeof(IResponseAware)) -- a CanBeCastTo filter against that very interface -- so the constraint holds by construction. The closed type is only ever named inside an emitted [DynamicDependency]; it is never instantiated here.")]
    private static AttributeArg closedApplierRoot(Type responseAwareType)
    {
        return AttributeArg.Type(typeof(Applier<>).MakeGenericType(responseAwareType));
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
        RegistryType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == HttpEndpointRegistry.GeneratedTypeName);
        return RegistryType != null;
    }
}

/// <summary>
///     Writes the body of <see cref="HttpEndpointRegistry.EndpointTypes" /> as a single
///     <c>typeof(...)</c> array literal — fully resolved at codegen time, no reflection at runtime.
/// </summary>
internal class WriteEndpointTypesFrame : SyncFrame
{
    private readonly Type[] _types;

    public WriteEndpointTypesFrame(Type[] types)
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

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_types.Length == 0)
        {
            writer.Write($"System.Array.Empty<System.Type>()");
        }
        else
        {
            // typeof<> in F# cannot resolve F# module types (only class/record/DU types).
            // Use Type.GetType with the assembly-qualified name — works for both.
            var quotedNames = string.Join("; ", _types.Select(t => $"\"{t.AssemblyQualifiedName}\""));
            writer.Write($"[| {quotedNames} |] |> Array.choose (fun n -> System.Type.GetType(n) |> Option.ofObj)");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
