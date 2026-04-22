using System.Diagnostics;
using System.Reflection;
using System.ServiceModel;
using Grpc.Core;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using ProtoBuf.Grpc;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Persistence;

namespace Wolverine.Grpc;

/// <summary>
///     Represents a hand-written code-first gRPC service class (a concrete class whose name ends in
///     <c>GrpcService</c> or that carries <see cref="WolverineGrpcServiceAttribute"/>) for which Wolverine
///     generates a thin delegation wrapper at startup. The wrapper implements the same
///     <c>[ServiceContract]</c> interface as the user's class, weaves any <c>Validate</c> /
///     <c>[WolverineBefore]</c> / <c>[WolverineAfter]</c> middleware, then delegates each call to
///     an injected instance of the user's class.
/// </summary>
public class HandWrittenGrpcServiceChain : Chain<HandWrittenGrpcServiceChain, ModifyHandWrittenGrpcServiceChainAttribute>,
    ICodeFile
{
    private GeneratedType? _generatedType;
    private Type? _generatedRuntimeType;
    private MethodInfo[]? _discoveredBefores;
    private MethodInfo[]? _discoveredAfters;

    /// <summary>The user's concrete service class.</summary>
    public Type ServiceClassType { get; }

    /// <summary>The <c>[ServiceContract]</c> interface the service class implements.</summary>
    public Type ServiceContractType { get; }

    /// <summary>The C# identifier for the generated wrapper type.</summary>
    public string TypeName { get; }

    /// <summary>The RPC methods discovered on <see cref="ServiceContractType"/> and classified by shape.</summary>
    public IReadOnlyList<HandWrittenRpcMethod> SupportedMethods { get; }

    /// <summary>The runtime <see cref="Type"/> of the generated wrapper once compiled. Null before compilation.</summary>
    public Type? GeneratedType => _generatedRuntimeType;

    internal string? SourceCode => _generatedType?.SourceCode;

    public HandWrittenGrpcServiceChain(Type serviceClassType)
    {
        ServiceClassType = serviceClassType ?? throw new ArgumentNullException(nameof(serviceClassType));

        ServiceContractType = FindServiceContractInterface(serviceClassType)
            ?? throw new InvalidOperationException(
                $"Hand-written gRPC service {serviceClassType.FullNameInCode()} must implement a "
                + "[ServiceContract] interface so Wolverine can generate a delegation wrapper. "
                + "Add a [ServiceContract] interface to the class or use [WolverineGrpcService] on "
                + "the interface directly (code-first generated implementation path).");

        SupportedMethods = DiscoverMethods(ServiceContractType).ToArray();
        TypeName = ResolveTypeName(serviceClassType);
        Description =
            $"Generated delegation wrapper for hand-written gRPC service {serviceClassType.FullNameInCode()} "
            + $"(contract: {ServiceContractType.FullNameInCode()})";
    }

    // --- Chain<> abstract member implementations ---

    public override string Description { get; }
    public override MiddlewareScoping Scoping => MiddlewareScoping.Grpc;
    public override IdempotencyStyle Idempotency { get; set; } = IdempotencyStyle.None;
    public override Type? InputType() => null;
    public override bool ShouldFlushOutgoingMessages() => false;
    public override bool RequiresOutbox() => false;
    public override MethodCall[] HandlerCalls() => [];
    public override bool HasAttribute<T>() => ServiceClassType.HasAttribute<T>();
    public override void ApplyParameterMatching(MethodCall call) { }

    public override bool TryInferMessageIdentity(out PropertyInfo? property)
    {
        property = null;
        return false;
    }

    public override bool TryFindVariable(string valueName, ValueSource source, Type valueType,
        out Variable variable)
    {
        variable = default!;
        return false;
    }

    public override Frame[] AddStopConditionIfNull(Variable variable) => [];
    public override void UseForResponse(MethodCall methodCall) { }

    // --- Middleware discovery (scans the service class, same pattern as GrpcServiceChain) ---

    /// <summary>
    ///     Static methods on <see cref="ServiceClassType"/> matching <c>Validate</c> / <c>Before</c>
    ///     naming conventions or carrying <c>[WolverineBefore]</c>. Includes the <c>Validate → Status?</c>
    ///     short-circuit hook. Sorted ordinally for byte-stable generated source.
    /// </summary>
    public IReadOnlyList<MethodInfo> DiscoveredBefores
        => _discoveredBefores ??= MiddlewarePolicy
            .FilterMethods<WolverineBeforeAttribute>(this, ServiceClassType.GetMethods(),
                MiddlewarePolicy.BeforeMethodNames)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     Static methods on <see cref="ServiceClassType"/> matching <c>After</c> naming conventions
    ///     or carrying <c>[WolverineAfter]</c>. Same scope and sort rules as <see cref="DiscoveredBefores"/>.
    /// </summary>
    public IReadOnlyList<MethodInfo> DiscoveredAfters
        => _discoveredAfters ??= MiddlewarePolicy
            .FilterMethods<WolverineAfterAttribute>(this, ServiceClassType.GetMethods(),
                MiddlewarePolicy.AfterMethodNames)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

    // --- ICodeFile ---

    string ICodeFile.FileName => TypeName;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        if (_generatedType != null) return;

        assembly.ReferenceAssembly(ServiceClassType.Assembly);
        assembly.ReferenceAssembly(ServiceContractType.Assembly);
        assembly.ReferenceAssembly(typeof(IMessageBus).Assembly);
        assembly.ReferenceAssembly(typeof(CallContext).Assembly);

        // The generated class implements the service contract interface from scratch.
        // The user's service class is injected as _inner; IMessageBus as _bus for middleware frames.
        _generatedType = assembly.AddType(TypeName, ServiceContractType);

        // Inject IServiceProvider rather than the service class directly. The delegation frame
        // resolves the inner instance via ActivatorUtilities.GetServiceOrCreateInstance, which
        // works whether or not ServiceClassType is explicitly registered in DI — avoiding the need
        // for callers to add a manual services.AddTransient<ServiceClassType>() registration.
        var spField = new InjectedField(typeof(IServiceProvider), "serviceProvider");
        _generatedType.AllInjectedFields.Add(spField);

        var befores = DiscoveredBefores;
        var afters = DiscoveredAfters;

        foreach (var rpc in SupportedMethods)
        {
            var generatedMethod = _generatedType.MethodFor(rpc.Method.Name);

            // Before-frames require a concrete TRequest in scope. Skip for bidi methods where
            // the first parameter is IAsyncEnumerable<T> rather than a single message instance.
            // Also skip any before whose non-CallContext parameters don't match this RPC method's
            // request type — a Validate(OrderRequest) must not fire on an InvoiceRequest RPC method.
            if (rpc.Kind != HandWrittenMethodKind.BidirectionalStreaming)
            {
                var rpcRequestType = rpc.Method.GetParameters()[0].ParameterType;

                foreach (var before in befores)
                {
                    if (!IsBeforeApplicable(before, rpcRequestType)) continue;

                    var call = new MethodCall(ServiceClassType, before);
                    generatedMethod.Frames.Add(call);

                    var statusVar = call.Creates.FirstOrDefault(v => v.VariableType == typeof(Status?));
                    if (statusVar != null)
                        generatedMethod.Frames.Add(new GrpcValidateShortCircuitFrame(statusVar));
                }
            }

            var hasAfters = afters.Count > 0 && rpc.Kind == HandWrittenMethodKind.Unary;
            if (hasAfters)
                generatedMethod.AsyncMode = AsyncMode.AsyncTask;

            generatedMethod.Frames.Add(new DelegateToInnerServiceFrame(rpc.Method, spField, ServiceClassType, hasAfters));

            if (hasAfters)
            {
                foreach (var after in afters)
                    generatedMethod.Frames.Add(new MethodCall(ServiceClassType, after));
            }
        }
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        var found = this.As<ICodeFile>().AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        Debug.WriteLine(_generatedType?.SourceCode);

        _generatedRuntimeType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == TypeName)
                                ?? assembly.GetTypes().FirstOrDefault(x => x.Name == TypeName);

        return _generatedRuntimeType != null;
    }

    // --- Static helpers ---

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="before"/> can be called in the context of an RPC
    ///     method whose first parameter is <paramref name="rpcRequestType"/>. A before method is applicable
    ///     when all of its non-<see cref="CallContext"/> parameters are assignable from
    ///     <paramref name="rpcRequestType"/>. This prevents a <c>Validate(OrderRequest)</c> from firing
    ///     inside an <c>InvoiceRequest</c> RPC method where no <c>OrderRequest</c> variable is in scope.
    /// </summary>
    private static bool IsBeforeApplicable(MethodInfo before, Type rpcRequestType)
    {
        foreach (var p in before.GetParameters())
        {
            if (p.ParameterType == typeof(CallContext)) continue;
            if (!p.ParameterType.IsAssignableFrom(rpcRequestType)) return false;
        }
        return true;
    }

    /// <summary>
    ///     Derives the wrapper type name from the service class. Strips the <c>GrpcService</c> suffix
    ///     (if present) and appends <c>GrpcHandler</c>:
    ///     <c>OrderGrpcService</c> → <c>OrderGrpcHandler</c>,
    ///     <c>MyOrderManager</c> → <c>MyOrderManagerGrpcHandler</c>.
    /// </summary>
    public static string ResolveTypeName(Type serviceClassType)
    {
        var name = serviceClassType.Name;
        if (name.EndsWith("GrpcService", StringComparison.Ordinal))
            return name[..^"GrpcService".Length] + "GrpcHandler";
        return name + "GrpcHandler";
    }

    /// <summary>
    ///     Returns the first <c>[ServiceContract]</c> interface on <paramref name="serviceClassType"/>,
    ///     or <c>null</c> if none exists.
    /// </summary>
    public static Type? FindServiceContractInterface(Type serviceClassType)
        => serviceClassType.GetInterfaces()
            .FirstOrDefault(i => i.IsDefined(typeof(ServiceContractAttribute), inherit: false));

    /// <summary>
    ///     Discovers and classifies the RPC methods on <paramref name="contractInterface"/>.
    ///     Methods whose signatures don't match a supported protobuf-net.Grpc shape are skipped.
    ///     Results are sorted by method name for byte-stable generated source.
    /// </summary>
    public static IEnumerable<HandWrittenRpcMethod> DiscoverMethods(Type contractInterface)
    {
        var results = new List<HandWrittenRpcMethod>();

        foreach (var method in contractInterface.GetMethods())
        {
            var kind = ClassifyMethod(method);
            if (kind == null) continue;
            results.Add(new HandWrittenRpcMethod(method, kind.Value));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.Method.Name, b.Method.Name));
        return results;
    }

    /// <summary>
    ///     Classifies a single interface method against the protobuf-net.Grpc code-first shapes.
    ///     Handles the bidi case (first parameter is <c>IAsyncEnumerable&lt;TRequest&gt;</c>)
    ///     in addition to the unary and server-streaming shapes that
    ///     <see cref="CodeFirstGrpcServiceChain"/> already supports.
    ///     Returns <c>null</c> when the method doesn't match any recognised shape.
    /// </summary>
    private static HandWrittenMethodKind? ClassifyMethod(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0 || parameters.Length > 2) return null;

        // Optional second parameter must be CallContext.
        if (parameters.Length == 2 && parameters[1].ParameterType != typeof(CallContext)) return null;

        var firstParam = parameters[0].ParameterType;

        // Bidi: first param is IAsyncEnumerable<TRequest>
        if (firstParam.IsGenericType && firstParam.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            if (method.ReturnType == typeof(Task)) return HandWrittenMethodKind.BidirectionalStreaming;
            if (method.ReturnType.IsGenericType &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                return HandWrittenMethodKind.BidirectionalStreaming;
            return null;
        }

        // Unary and server-streaming: first param must be a concrete message type.
        if (!firstParam.IsClass || firstParam.IsAbstract) return null;
        if (!method.ReturnType.IsGenericType) return null;

        var returnOpen = method.ReturnType.GetGenericTypeDefinition();
        if (returnOpen == typeof(Task<>)) return HandWrittenMethodKind.Unary;
        if (returnOpen == typeof(IAsyncEnumerable<>)) return HandWrittenMethodKind.ServerStreaming;

        return null;
    }
}

/// <summary>
///     Classifies a hand-written code-first gRPC interface method by its protobuf-net.Grpc C# shape.
///     Extends the shapes supported by <see cref="CodeFirstGrpcServiceChain"/> with
///     <see cref="BidirectionalStreaming"/> for the <c>IAsyncEnumerable&lt;TReq&gt; → IAsyncEnumerable&lt;TResp&gt;</c>
///     bidi pattern that only hand-written services implement directly.
/// </summary>
public enum HandWrittenMethodKind
{
    /// <summary>Unary: <c>Task&lt;TResponse&gt; Name(TRequest[, CallContext])</c>.</summary>
    Unary,

    /// <summary>Server-streaming: <c>IAsyncEnumerable&lt;TResponse&gt; Name(TRequest[, CallContext])</c>.</summary>
    ServerStreaming,

    /// <summary>
    ///     Bidi streaming: <c>IAsyncEnumerable&lt;TResponse&gt; Name(IAsyncEnumerable&lt;TRequest&gt;[, CallContext])</c>
    ///     or <c>Task Name(IAsyncEnumerable&lt;TRequest&gt;[, CallContext])</c>. The hand-written service
    ///     implements the full bidi loop; Wolverine generates a pure delegation wrapper. Before-frames
    ///     are not woven for bidi — there is no single request instance in scope before the loop begins.
    /// </summary>
    BidirectionalStreaming
}

/// <summary>A single hand-written RPC method paired with its <see cref="HandWrittenMethodKind"/>.</summary>
/// <param name="Method">Reflection handle to the interface method.</param>
/// <param name="Kind">The RPC shape Wolverine classified this method as.</param>
public readonly record struct HandWrittenRpcMethod(MethodInfo Method, HandWrittenMethodKind Kind);

/// <summary>
///     Resolves the hand-written service instance via
///     <c>ActivatorUtilities.GetServiceOrCreateInstance</c> and delegates the RPC call to it.
///     Using <see cref="IServiceProvider"/> rather than a direct constructor injection of the
///     service class avoids requiring an explicit DI registration for the inner type — the
///     activator will construct it from the request-scoped provider if it isn't already registered.
///     For unary methods with after-frames the return value is awaited so after-frames can run
///     before the response is sent. For server-streaming and bidi shapes the inner call is returned
///     directly — the inner service owns the streaming lifecycle.
/// </summary>
internal sealed class DelegateToInnerServiceFrame : SyncFrame
{
    private readonly MethodInfo _interfaceMethod;
    private readonly InjectedField _spField;
    private readonly Type _serviceClassType;
    private readonly bool _awaitResult;

    public DelegateToInnerServiceFrame(MethodInfo interfaceMethod, InjectedField spField,
        Type serviceClassType, bool awaitResult)
    {
        _interfaceMethod = interfaceMethod;
        _spField = spField;
        _serviceClassType = serviceClassType;
        _awaitResult = awaitResult;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var parameters = _interfaceMethod.GetParameters();
        var args = string.Join(", ", parameters.Select((p, i) => p.Name ?? $"arg{i}"));

        var innerType = _serviceClassType.FullNameInCode();
        writer.Write(
            $"var inner = {typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilities).FullNameInCode()}"
            + $".GetServiceOrCreateInstance<{innerType}>({_spField.Usage});");

        var call = $"inner.{_interfaceMethod.Name}({args})";

        if (_awaitResult)
        {
            writer.Write($"var result = await {call};");
            Next?.GenerateCode(method, writer);
            writer.Write("return result;");
        }
        else
        {
            writer.Write($"return {call};");
            Next?.GenerateCode(method, writer);
        }
    }
}
