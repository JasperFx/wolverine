using System.Reflection;
using System.ServiceModel;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Runtime;

namespace Wolverine.Grpc;

/// <summary>
///     Discovers proto-first and code-first Wolverine gRPC services, builds chain instances for them,
///     and plugs their generated wrapper types into the Wolverine code-generation pipeline.
///     Mirrors the role of <c>HandlerGraph</c> / <c>HttpGraph</c> for their respective chain types.
/// </summary>
public class GrpcGraph : ICodeFileCollectionWithServices, IDescribeMyself
{
    private readonly List<GrpcServiceChain> _chains = [];
    private readonly List<CodeFirstGrpcServiceChain> _codeFirstChains = [];
    private readonly List<HandWrittenGrpcServiceChain> _handWrittenChains = [];
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

    /// <summary>Proto-first service chains (abstract stub → generated wrapper).</summary>
    public IReadOnlyList<GrpcServiceChain> Chains => _chains;

    /// <summary>Code-first service chains (<c>[ServiceContract]</c> interface → generated implementation).</summary>
    public IReadOnlyList<CodeFirstGrpcServiceChain> CodeFirstChains => _codeFirstChains;

    /// <summary>Hand-written service chains (concrete service class → generated delegation wrapper).</summary>
    public IReadOnlyList<HandWrittenGrpcServiceChain> HandWrittenChains => _handWrittenChains;

    public IReadOnlyList<ICodeFile> BuildFiles() => [.._chains, .._codeFirstChains, .._handWrittenChains];

    /// <summary>
    ///     Scans the assemblies already registered with Wolverine and builds chains for every
    ///     discovered proto-first stub and code-first service contract. Applies any middleware
    ///     types and <see cref="IChainPolicy"/> implementations registered in
    ///     <paramref name="grpcOptions"/> and in <see cref="WolverineOptions.Policies"/>.
    /// </summary>
    public void DiscoverServices(WolverineGrpcOptions grpcOptions)
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

        var contracts = FindCodeFirstServiceContracts(_options.Assemblies).ToArray();
        logger.LogInformation(
            "Found {Count} code-first Wolverine gRPC service contracts in assemblies {Assemblies}",
            contracts.Length,
            _options.Assemblies.Select(x => x.GetName().Name!).Join(", "));

        foreach (var contract in contracts)
        {
            CodeFirstGrpcServiceChain.AssertNoConcreteImplementationConflicts(contract, _options.Assemblies);
            _codeFirstChains.Add(new CodeFirstGrpcServiceChain(contract));
        }

        var handWritten = FindHandWrittenServiceClasses(_options.Assemblies).ToArray();
        logger.LogInformation(
            "Found {Count} hand-written Wolverine gRPC service classes in assemblies {Assemblies}",
            handWritten.Length,
            _options.Assemblies.Select(x => x.GetName().Name!).Join(", "));

        foreach (var serviceClass in handWritten)
        {
            _handWrittenChains.Add(new HandWrittenGrpcServiceChain(serviceClass));
        }

        // Apply policy-registered middleware and IChainPolicy implementations.
        var chainableChains = (IReadOnlyList<IChain>)[.._chains, .._codeFirstChains, .._handWrittenChains];

        grpcOptions.Middleware.Apply(chainableChains, Rules, Container);

        foreach (var policy in _options.Policies.OfType<IChainPolicy>())
        {
            policy.Apply(chainableChains, Rules, Container);
        }

        foreach (var policy in grpcOptions.Policies)
        {
            policy.Apply(_chains, _codeFirstChains, _handWrittenChains, Rules, Container);
        }
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

    /// <summary>
    ///     A code-first service contract is an interface annotated with both
    ///     <see cref="ServiceContractAttribute"/> (protobuf-net.Grpc) and
    ///     <see cref="WolverineGrpcServiceAttribute"/>. Wolverine generates a concrete implementation
    ///     at startup that forwards each method to the message bus.
    /// </summary>
    public static IEnumerable<Type> FindCodeFirstServiceContracts(IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(IsCodeFirstServiceContract);
    }

    private static bool IsCodeFirstServiceContract(Type type)
    {
        if (!type.IsInterface) return false;
        if (type.IsGenericTypeDefinition) return false;
        if (!type.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false)) return false;

        return type.IsDefined(typeof(ServiceContractAttribute), inherit: false);
    }

    /// <summary>
    ///     A hand-written service class is a concrete, non-abstract type that matches the code-first
    ///     discovery predicate (name ends in <c>GrpcService</c> or carries
    ///     <see cref="WolverineGrpcServiceAttribute"/>) AND implements at least one
    ///     <c>[ServiceContract]</c> interface. Classes whose service contract interface is itself
    ///     annotated with <see cref="WolverineGrpcServiceAttribute"/> are excluded — those are handled
    ///     by the <see cref="CodeFirstGrpcServiceChain"/> generated-implementation path instead.
    /// </summary>
    public static IEnumerable<Type> FindHandWrittenServiceClasses(IEnumerable<Assembly> assemblies)
    {
        return assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(IsHandWrittenServiceClass);
    }

    private static bool IsHandWrittenServiceClass(Type type)
    {
        if (!type.IsClass || type.IsAbstract) return false;
        if (type.IsGenericTypeDefinition) return false;

        // Must match the code-first discovery predicate.
        if (!type.Name.EndsWith("GrpcService", StringComparison.Ordinal)
            && !type.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false))
            return false;

        // Must implement a [ServiceContract] interface.
        var contract = HandWrittenGrpcServiceChain.FindServiceContractInterface(type);
        if (contract == null) return false;

        // If the contract interface itself carries [WolverineGrpcService], the generated-implementation
        // path owns this contract — don't also create a hand-written chain for the concrete class.
        if (contract.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false)) return false;

        // Proto-first stubs (abstract classes inheriting a proto base) are handled separately.
        // Concrete classes with a proto base are caught by AssertNoConcreteProtoStubs.
        return true;
    }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription(this);

        var protoList = description.AddChildSet("Proto-First Services");
        protoList.SummaryColumns = ["StubType", "ProtoServiceBase", "UnaryMethodCount"];

        foreach (var chain in _chains)
        {
            var row = new OptionsDescription(chain);
            row.AddValue("StubType", chain.StubType.FullNameInCode());
            row.AddValue("ProtoServiceBase", chain.ProtoServiceBase.FullNameInCode());
            row.AddValue("UnaryMethodCount", chain.UnaryMethods.Count);
            protoList.Rows.Add(row);
        }

        var codeFirstList = description.AddChildSet("Code-First Services");
        codeFirstList.SummaryColumns = ["ContractType", "GeneratedTypeName", "MethodCount"];

        foreach (var chain in _codeFirstChains)
        {
            var row = new OptionsDescription(chain);
            row.AddValue("ContractType", chain.ServiceContractType.FullNameInCode());
            row.AddValue("GeneratedTypeName", chain.TypeName);
            row.AddValue("MethodCount", chain.SupportedMethods.Count);
            codeFirstList.Rows.Add(row);
        }

        var handWrittenList = description.AddChildSet("Hand-Written Services");
        handWrittenList.SummaryColumns = ["ServiceClass", "ContractType", "WrapperTypeName", "MethodCount"];

        foreach (var chain in _handWrittenChains)
        {
            var row = new OptionsDescription(chain);
            row.AddValue("ServiceClass", chain.ServiceClassType.FullNameInCode());
            row.AddValue("ContractType", chain.ServiceContractType.FullNameInCode());
            row.AddValue("WrapperTypeName", chain.TypeName);
            row.AddValue("MethodCount", chain.SupportedMethods.Count);
            handWrittenList.Rows.Add(row);
        }

        return description;
    }
}
