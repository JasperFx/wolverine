using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Grpc;

/// <summary>
///     Discovers proto-first Wolverine gRPC services, builds <see cref="GrpcServiceChain"/> instances
///     for them, and plugs their generated wrapper types into the Wolverine code-generation pipeline.
///     Mirrors the role of <c>HandlerGraph</c> / <c>HttpGraph</c> for their respective chain types.
/// </summary>
public class GrpcGraph : ICodeFileCollectionWithServices, IDescribeMyself
{
    private readonly List<GrpcServiceChain> _chains = [];
    private readonly WolverineOptions _options;

    public GrpcGraph(WolverineOptions options, IServiceContainer container)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Container = container ?? throw new ArgumentNullException(nameof(container));
        Rules = options.CodeGeneration;
    }

    internal IServiceContainer Container { get; }

    [IgnoreDescription]
    public GenerationRules Rules { get; }

    public string ChildNamespace => "WolverineHandlers";

    public IReadOnlyList<GrpcServiceChain> Chains => _chains;

    public IReadOnlyList<ICodeFile> BuildFiles() => _chains;

    /// <summary>
    ///     Scans the assemblies already registered with Wolverine and builds a
    ///     <see cref="GrpcServiceChain"/> for every discovered proto-first stub.
    /// </summary>
    public void DiscoverServices()
    {
        var logger = Container.GetInstance<ILogger<GrpcGraph>>();

        AssertNoConcreteProtoStubs(_options.Assemblies);

        var stubs = FindProtoFirstStubs(_options.Assemblies).ToArray();
        logger.LogInformation(
            "Found {Count} proto-first Wolverine gRPC services in assemblies {Assemblies}",
            stubs.Length,
            _options.Assemblies.Select(x => x.GetName().Name!).Join(", "));

        foreach (var stub in stubs)
        {
            _chains.Add(new GrpcServiceChain(stub, this));
        }

        DisambiguateCollidingTypeNames(_chains);
    }

    /// <summary>
    ///     Post-discovery pass that guarantees unique <see cref="GrpcServiceChain.TypeName"/>s
    ///     across the graph. Two proto services sharing a simple name (e.g., a <c>Greeter</c>
    ///     in each of two bounded contexts) would otherwise both generate
    ///     <c>GreeterGrpcHandler</c> into the same <c>WolverineHandlers</c> child namespace,
    ///     and <c>AttachTypesSynchronously</c> would pick whichever exported type the CLR
    ///     handed back first — an order that is not guaranteed across assemblies. Mirrors
    ///     the pattern in <c>HandlerGraph</c> (issue #2004) but uses a stable hash of the
    ///     stub's <see cref="Type.FullName"/> as the qualifier, since gRPC stub simple names
    ///     are not reliably unique (users often pick ergonomic type names).
    /// </summary>
    internal static void DisambiguateCollidingTypeNames(IList<GrpcServiceChain> chains)
    {
        var collisions = chains
            .GroupBy(c => c.TypeName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToArray();

        foreach (var group in collisions)
        {
            foreach (var chain in group)
            {
                var stubFullName = chain.StubType.FullName ?? chain.StubType.Name;
                var hash = (uint)stubFullName.GetDeterministicHashCode();
                chain.ApplyDisambiguatedTypeName($"{chain.ProtoServiceName}GrpcHandler_{hash:x8}");
            }
        }
    }

    /// <summary>
    ///     Guards against a common misuse: a user puts <see cref="WolverineGrpcServiceAttribute"/> on a
    ///     concrete class that still inherits a proto-generated base. Without making the class abstract,
    ///     Wolverine cannot emit a subclass to override the unary methods, so the service would silently
    ///     do nothing at runtime. We fail fast with an actionable message instead.
    /// </summary>
    public static void AssertNoConcreteProtoStubs(IEnumerable<Assembly> assemblies)
        => AssertNoConcreteProtoStubs(assemblies.SelectMany(a => a.GetExportedTypes()));

    /// <summary>
    ///     Type-list overload — used by unit tests to feed in offender types that are deliberately
    ///     hidden from assembly scanning (so they don't break unrelated discovery tests).
    /// </summary>
    public static void AssertNoConcreteProtoStubs(IEnumerable<Type> types)
    {
        var offenders = types.Where(IsConcreteProtoStubMisuse).ToList();

        if (offenders.Count == 0) return;

        var details = offenders
            .Select(t => $"  - {t.FullNameInCode()} (proto base: {GrpcServiceChain.FindProtoServiceBase(t)!.FullNameInCode()})")
            .Join(Environment.NewLine);

        throw new InvalidOperationException(
            "[WolverineGrpcService] can only be applied to abstract stubs for proto-first code generation. "
            + "Wolverine generates a concrete subclass per stub to override the proto-generated unary methods — "
            + "a concrete stub leaves no method to override and would silently no-op at runtime."
            + Environment.NewLine
            + "Offending type(s):"
            + Environment.NewLine
            + details
            + Environment.NewLine
            + "Fix: mark the stub 'abstract', e.g. 'public abstract class MyGrpcService : MyProto.MyProtoBase;'. "
            + "For code-first services (no .proto), drop the proto base class and use a plain concrete class instead.");
    }

    /// <summary>
    ///     Predicate: is this type a concrete class carrying <see cref="WolverineGrpcServiceAttribute"/>
    ///     and inheriting a proto-generated service base? That combination is always a misuse because
    ///     Wolverine needs an abstract stub to generate an override-only subclass against.
    /// </summary>
    public static bool IsConcreteProtoStubMisuse(Type type)
    {
        if (!type.IsClass || type.IsAbstract) return false;
        if (type.IsGenericTypeDefinition) return false;
        if (!type.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false)) return false;

        return GrpcServiceChain.FindProtoServiceBase(type) != null;
    }

    /// <summary>
    ///     A proto-first stub is an abstract, non-generic class annotated with
    ///     <see cref="WolverineGrpcServiceAttribute"/> whose inheritance chain contains a
    ///     proto-generated service base (detected via <c>[BindServiceMethod]</c>).
    /// </summary>
    public static IEnumerable<Type> FindProtoFirstStubs(IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(IsProtoFirstStub);
    }

    private static bool IsProtoFirstStub(Type type)
    {
        if (!type.IsClass || !type.IsAbstract) return false;
        if (type.IsGenericTypeDefinition) return false;
        if (!type.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false)) return false;

        return GrpcServiceChain.FindProtoServiceBase(type) != null;
    }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription(this);
        var list = description.AddChildSet("Services");
        list.SummaryColumns = ["StubType", "ProtoServiceBase", "UnaryMethodCount"];

        foreach (var chain in _chains)
        {
            var row = new OptionsDescription(chain);
            row.AddValue("StubType", chain.StubType.FullNameInCode());
            row.AddValue("ProtoServiceBase", chain.ProtoServiceBase.FullNameInCode());
            row.AddValue("UnaryMethodCount", chain.UnaryMethods.Count);
            list.Rows.Add(row);
        }

        return description;
    }
}
