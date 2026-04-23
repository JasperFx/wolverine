using System.Reflection;
using System.ServiceModel;
using Grpc.Core;
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
///     Represents a code-first gRPC service contract (a <c>[ServiceContract]</c> interface marked
///     with <see cref="WolverineGrpcServiceAttribute"/>) for which Wolverine will generate a concrete
///     implementing class at startup. Each method on the interface that matches a supported
///     protobuf-net.Grpc signature is forwarded to the Wolverine message bus:
///     <list type="bullet">
///       <item>Unary <c>Task&lt;TResponse&gt; Name(TRequest[, CallContext])</c> →
///             <see cref="IMessageBus.InvokeAsync{T}"/></item>
///       <item>Server-streaming <c>IAsyncEnumerable&lt;TResponse&gt; Name(TRequest[, CallContext])</c> →
///             <see cref="IMessageBus.StreamAsync{TResponse}"/></item>
///     </list>
/// </summary>
public class CodeFirstGrpcServiceChain : Chain<CodeFirstGrpcServiceChain, ModifyCodeFirstGrpcServiceChainAttribute>,
    ICodeFile
{
    private static readonly PropertyInfo CallContextCancellationTokenProperty =
        typeof(CallContext).GetProperty(nameof(CallContext.CancellationToken))!;

    private GeneratedType? _generatedType;
    private Type? _generatedRuntimeType;
    private IEnumerable<Assembly>? _applicationAssemblies;

    /// <summary>
    ///     The <c>[ServiceContract]</c> interface annotated with <see cref="WolverineGrpcServiceAttribute"/>
    ///     that this chain was built from.
    /// </summary>
    public Type ServiceContractType { get; }

    /// <summary>
    ///     The C# identifier for the generated implementation type.
    ///     Derived from the interface name with the leading <c>I</c> stripped and <c>GrpcHandler</c> appended:
    ///     <c>IPingService</c> → <c>PingServiceGrpcHandler</c>.
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    ///     The code-first RPC methods discovered on <see cref="ServiceContractType"/> and classified
    ///     as Wolverine-supported shapes.
    /// </summary>
    public IReadOnlyList<CodeFirstRpcMethod> SupportedMethods { get; }

    /// <summary>
    ///     The runtime <see cref="Type"/> of the generated implementation once compiled. Null before compilation.
    /// </summary>
    public Type? GeneratedType => _generatedRuntimeType;

    internal string? SourceCode => _generatedType?.SourceCode;

    public CodeFirstGrpcServiceChain(Type serviceContractType)
    {
        ServiceContractType = serviceContractType ?? throw new ArgumentNullException(nameof(serviceContractType));
        SupportedMethods = DiscoverMethods(serviceContractType).ToArray();
        TypeName = ResolveTypeName(serviceContractType);
        Description = $"Generated code-first gRPC service implementation for {serviceContractType.FullNameInCode()}";
    }

    // --- Middleware discovery (scans handler types for each RPC's request message) ---
    //
    // In Wolverine, static Before/Validate/After hook methods live on the handler class — the same
    // class that owns the Handle/HandleAsync/Consume/ConsumeAsync method for that message. Code-first
    // gRPC chains follow this idiom: for each RPC method, the chain scans the application assemblies
    // for types that handle the request type and checks them for middleware hooks.
    //
    // Assembly scanning is used instead of HandlerGraph because AssembleTypes fires before
    // HandlerGraph.Compile() runs (MapWolverineGrpcServices is called in the middleware pipeline,
    // before WolverineRuntime.StartAsync). ApplicationAssemblies is set by GrpcGraph.DiscoverServices
    // immediately after the chain is constructed. If it is null (unit-test contexts that construct
    // the chain directly), HandlerTypesFor yields nothing and the before/after path is a no-op.

    private static readonly HashSet<string> HandlerMethodNames =
        new(StringComparer.Ordinal) { "Handle", "HandleAsync", "Consume", "ConsumeAsync" };

    /// <summary>
    ///     The application assemblies to scan for handler types. Set by <see cref="GrpcGraph.DiscoverServices"/>
    ///     after construction. Null in unit-test contexts that build the chain directly.
    /// </summary>
    internal IEnumerable<Assembly>? ApplicationAssemblies
    {
        get => _applicationAssemblies;
        set => _applicationAssemblies = value;
    }

    /// <summary>
    ///     Scans <see cref="ApplicationAssemblies"/> for classes that handle <paramref name="requestType"/>
    ///     (i.e. have a Handle/HandleAsync/Consume/ConsumeAsync method whose first parameter is
    ///     <paramref name="requestType"/>). Yields nothing when assemblies have not been set.
    /// </summary>
    private IEnumerable<Type> HandlerTypesFor(Type requestType)
    {
        if (_applicationAssemblies == null) yield break;
        foreach (var assembly in _applicationAssemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (!type.IsClass) continue;
                if (type.GetMethods().Any(m =>
                        HandlerMethodNames.Contains(m.Name)
                        && m.GetParameters().Length > 0
                        && m.GetParameters()[0].ParameterType == requestType))
                    yield return type;
            }
        }
    }

    // --- Chain<> abstract member implementations ---

    public override string Description { get; }
    public override MiddlewareScoping Scoping => MiddlewareScoping.Grpc;
    public override IdempotencyStyle Idempotency { get; set; } = IdempotencyStyle.None;
    public override Type? InputType() => null;
    public override bool ShouldFlushOutgoingMessages() => false;
    public override bool RequiresOutbox() => false;
    public override MethodCall[] HandlerCalls() => [];
    public override bool HasAttribute<T>() => ServiceContractType.HasAttribute<T>();
    public override void ApplyParameterMatching(MethodCall call) { }

    public override bool TryInferMessageIdentity(out PropertyInfo? property)
    {
        property = null;
        return false;
    }

    public override bool TryFindVariable(string valueName, ValueSource source, Type valueType, out Variable variable)
    {
        variable = default!;
        return false;
    }

    public override Frame[] AddStopConditionIfNull(Variable variable) => [];
    public override void UseForResponse(MethodCall methodCall) { }

    // ---

    string ICodeFile.FileName => TypeName;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        if (_generatedType != null) return;

        assembly.ReferenceAssembly(ServiceContractType.Assembly);
        assembly.ReferenceAssembly(typeof(IMessageBus).Assembly);
        assembly.ReferenceAssembly(typeof(CallContext).Assembly);

        // assembly.AddType detects that ServiceContractType is an interface and calls Implements() —
        // the generated class will declare: public sealed class PingServiceGrpcHandler : IPingService
        _generatedType = assembly.AddType(TypeName, ServiceContractType);

        // IMessageBus is injected directly. No WolverineGrpcServiceBase needed for generated types
        // since users only extend that to get Bus exposed on hand-written services.
        var busField = new InjectedField(typeof(IMessageBus), "bus");
        _generatedType.AllInjectedFields.Add(busField);

        foreach (var rpc in SupportedMethods)
        {
            var generatedMethod = _generatedType.MethodFor(rpc.Method.Name);

            // Register the IVariableSource so any frame that needs CancellationToken gets it
            // from context.CancellationToken rather than hardcoding the property access. The source
            // makes frames composable: they declare a dependency on CancellationToken and the source
            // resolves it from the CallContext argument, regardless of that argument's local name.
            var contextArg = generatedMethod.Arguments
                .FirstOrDefault(a => a.VariableType == typeof(CallContext));

            if (contextArg != null)
                generatedMethod.Sources.Add(new CallContextCancellationTokenSource(contextArg));

            // Global middleware befores (from grpc.AddMiddleware<T>()) — cloned per method
            // because Frame instances hold per-method mutable state (Next pointer, variable bindings).
            foreach (var frame in CloneFrames(Middleware))
                generatedMethod.Frames.Add(frame);

            // Per-method handler-type discovery: find the Wolverine handler class(es) for this
            // RPC's request type and scan them for [WolverineBefore]/[WolverineAfter] hooks.
            // This is the same idiom as HandlerChain — middleware lives on the handler class.
            var rpcRequestType = rpc.Method.GetParameters()[0].ParameterType;
            var handlerTypes = HandlerTypesFor(rpcRequestType).ToArray();

            var befores = handlerTypes
                .SelectMany(ht => MiddlewarePolicy.FilterMethods<WolverineBeforeAttribute>(
                                      this, ht.GetMethods(), MiddlewarePolicy.BeforeMethodNames))
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();

            var afters = handlerTypes
                .SelectMany(ht => MiddlewarePolicy.FilterMethods<WolverineAfterAttribute>(
                                      this, ht.GetMethods(), MiddlewarePolicy.AfterMethodNames))
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();

            foreach (var before in befores)
            {
                if (!IsBeforeApplicable(before, rpcRequestType)) continue;

                var call = new MethodCall(before.DeclaringType!, before);
                generatedMethod.Frames.Add(call);

                var statusVar = call.Creates.FirstOrDefault(v => v.VariableType == typeof(Status?));
                if (statusVar != null)
                    generatedMethod.Frames.Add(new GrpcValidateShortCircuitFrame(statusVar));
            }

            // AsyncMode must account for both discovered afters and global postprocessors.
            var hasAfters = (afters.Length > 0 || Postprocessors.Count > 0)
                            && rpc.Kind == CodeFirstMethodKind.Unary;
            if (hasAfters)
                generatedMethod.AsyncMode = AsyncMode.AsyncTask;

            switch (rpc.Kind)
            {
                case CodeFirstMethodKind.Unary:
                    generatedMethod.Frames.Add(new ForwardCodeFirstUnaryFrame(rpc.Method, busField));
                    break;

                case CodeFirstMethodKind.ServerStreaming:
                    generatedMethod.Frames.Add(new ForwardCodeFirstServerStreamFrame(rpc.Method, busField));
                    break;
            }

            // Discovered afters (unary only — streaming owns its own lifecycle).
            if (rpc.Kind == CodeFirstMethodKind.Unary)
            {
                foreach (var after in afters)
                    generatedMethod.Frames.Add(new MethodCall(after.DeclaringType!, after));
            }

            // Global middleware afters (from grpc.AddMiddleware<T>()) — cloned per method.
            foreach (var frame in CloneFrames(Postprocessors))
                generatedMethod.Frames.Add(frame);
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
        _generatedRuntimeType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == TypeName)
                                ?? assembly.GetTypes().FirstOrDefault(x => x.Name == TypeName);

        return _generatedRuntimeType != null;
    }

    /// <summary>
    ///     Reconstructs fresh <see cref="Frame"/> instances from <paramref name="source"/> so that
    ///     the same middleware registration can be woven into each of the N generated RPC methods
    ///     independently. Frame instances carry per-method mutable state (<c>Next</c> pointer, variable
    ///     bindings) and cannot be shared across method bodies — each method needs its own clones.
    ///     Supports <see cref="ConstructorFrame"/> (non-static middleware) and <see cref="MethodCall"/>
    ///     (static or instance method calls), which are the two types <see cref="MiddlewarePolicy"/>
    ///     produces. Unknown frame types are added as-is (best-effort).
    /// </summary>
    internal static IEnumerable<Frame> CloneFrames(IEnumerable<Frame> source)
    {
        foreach (var frame in source)
        {
            yield return frame switch
            {
                ConstructorFrame cf => new ConstructorFrame(cf.BuiltType, cf.Ctor) { Mode = cf.Mode },
                MethodCall mc => new MethodCall(mc.HandlerType, mc.Method),
                _ => frame
            };
        }
    }

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="before"/> can fire in the context of an RPC method
    ///     whose first parameter is <paramref name="rpcRequestType"/>. A before method is applicable when
    ///     all of its non-<see cref="CallContext"/> parameters are assignable from
    ///     <paramref name="rpcRequestType"/>. This prevents a <c>Validate(OrderRequest)</c> from being
    ///     woven into an <c>InvoiceRequest</c> RPC where no <c>OrderRequest</c> variable is in scope.
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
    ///     Derives the generated type name from the service contract interface.
    ///     Strips the leading <c>I</c> from the interface name (if followed by an uppercase letter) and
    ///     appends <c>GrpcHandler</c>: <c>IPingService</c> → <c>PingServiceGrpcHandler</c>.
    /// </summary>
    public static string ResolveTypeName(Type serviceContractType)
    {
        var name = serviceContractType.Name;
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        return name + "GrpcHandler";
    }

    /// <summary>
    ///     Discovers and classifies the RPC methods on <paramref name="serviceContractType"/>.
    ///     Methods whose signatures don't match a supported code-first protobuf-net.Grpc shape are skipped.
    ///     Results are sorted by method name so generated source is byte-stable across runs.
    /// </summary>
    public static IEnumerable<CodeFirstRpcMethod> DiscoverMethods(Type serviceContractType)
    {
        var results = new List<CodeFirstRpcMethod>();

        foreach (var method in serviceContractType.GetMethods())
        {
            var kind = ClassifyMethod(method);
            if (kind == null) continue;
            results.Add(new CodeFirstRpcMethod(method, kind.Value));
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.Method.Name, b.Method.Name));
        return results;
    }

    /// <summary>
    ///     Classifies a single interface method against the supported protobuf-net.Grpc code-first shapes.
    ///     Returns null if the method doesn't match any recognised shape.
    ///     <para>
    ///         Supported shapes (CallContext parameter is optional):
    ///         <list type="bullet">
    ///           <item>Unary: <c>Task&lt;TResponse&gt; Name(TRequest[, CallContext])</c></item>
    ///           <item>Server-streaming: <c>IAsyncEnumerable&lt;TResponse&gt; Name(TRequest[, CallContext])</c></item>
    ///         </list>
    ///     </para>
    /// </summary>
    private static CodeFirstMethodKind? ClassifyMethod(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0 || parameters.Length > 2) return null;

        // The first parameter must be a concrete message type (the request DTO).
        var requestType = parameters[0].ParameterType;
        if (!requestType.IsClass || requestType.IsAbstract) return null;

        // An optional second parameter must be CallContext.
        if (parameters.Length == 2 && parameters[1].ParameterType != typeof(CallContext)) return null;

        if (!method.ReturnType.IsGenericType) return null;

        var returnOpen = method.ReturnType.GetGenericTypeDefinition();

        // Unary: Task<TResponse>
        if (returnOpen == typeof(Task<>)) return CodeFirstMethodKind.Unary;

        // Server-streaming: IAsyncEnumerable<TResponse>
        if (returnOpen == typeof(IAsyncEnumerable<>)) return CodeFirstMethodKind.ServerStreaming;

        return null;
    }

    /// <summary>
    ///     Guards against applying <see cref="WolverineGrpcServiceAttribute"/> to both an interface
    ///     (the code-first codegen marker) and a concrete class that implements it (the hand-written
    ///     service marker). Both usages are valid independently; a conflict only arises when both are
    ///     present in the same assembly, which would produce two service registrations for the same contract.
    /// </summary>
    public static void AssertNoConcreteImplementationConflicts(Type serviceContractType,
        IEnumerable<Assembly> assemblies)
    {
        var offenders = assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.IsClass && !t.IsAbstract
                        && serviceContractType.IsAssignableFrom(t)
                        && t.IsDefined(typeof(WolverineGrpcServiceAttribute), inherit: false))
            .ToList();

        if (offenders.Count == 0) return;

        var details = offenders.Select(t => $"  - {t.FullNameInCode()}").Aggregate((a, b) => a + "\n" + b);

        throw new InvalidOperationException(
            $"Code-first gRPC service contract {serviceContractType.FullNameInCode()} is marked "
            + "[WolverineGrpcService] for Wolverine codegen, but one or more concrete implementations "
            + "of this interface are also marked [WolverineGrpcService] in the same assembly. "
            + "Remove [WolverineGrpcService] from the concrete class(es) and let Wolverine generate "
            + "the implementation, or remove it from the interface to keep the hand-written class."
            + "\nConflicting type(s):\n" + details);
    }
}

/// <summary>
///     Classifies a code-first gRPC method based on its protobuf-net.Grpc C# signature.
/// </summary>
public enum CodeFirstMethodKind
{
    /// <summary>
    ///     Unary: <c>Task&lt;TResponse&gt; Name(TRequest[, CallContext])</c>.
    ///     Forwarded via <see cref="IMessageBus.InvokeAsync{T}"/>.
    /// </summary>
    Unary,

    /// <summary>
    ///     Server-streaming: <c>IAsyncEnumerable&lt;TResponse&gt; Name(TRequest[, CallContext])</c>.
    ///     Forwarded via <see cref="IMessageBus.StreamAsync{TResponse}"/>.
    /// </summary>
    ServerStreaming
}

/// <summary>
///     A single code-first RPC method paired with its Wolverine-recognised <see cref="CodeFirstMethodKind"/>.
/// </summary>
/// <param name="Method">Reflection handle to the interface method.</param>
/// <param name="Kind">The RPC shape Wolverine classified this method as.</param>
public readonly record struct CodeFirstRpcMethod(MethodInfo Method, CodeFirstMethodKind Kind);

/// <summary>
///     JasperFx <see cref="IVariableSource"/> that resolves <see cref="CancellationToken"/> from the
///     <see cref="CallContext.CancellationToken"/> property on the code-first method's <c>context</c>
///     argument. Registered per-method in <see cref="CodeFirstGrpcServiceChain.AssembleTypes"/>:
///
///     <code>generatedMethod.Sources.Add(new CallContextCancellationTokenSource(contextArg));</code>
///
///     Any frame that declares a dependency on <see cref="CancellationToken"/> (via
///     <see cref="Frame.FindVariables"/>) will receive a <see cref="MemberAccessVariable"/> whose
///     <see cref="Variable.Usage"/> resolves to <c>context.CancellationToken</c> — regardless of what
///     the local parameter is named. This makes frames composable: they express a need for a
///     <see cref="CancellationToken"/> without knowing whether they are running in a code-first or
///     proto-first context.
/// </summary>
internal sealed class CallContextCancellationTokenSource : IVariableSource
{
    private readonly Variable _callContextArg;

    public CallContextCancellationTokenSource(Variable callContextArg)
    {
        _callContextArg = callContextArg;
    }

    public bool Matches(Type type) => type == typeof(CancellationToken);

    public Variable Create(Type type)
        => new MemberAccessVariable(_callContextArg,
            typeof(CallContext).GetProperty(nameof(CallContext.CancellationToken))!);
}

/// <summary>
///     Emits <c>return _bus.InvokeAsync&lt;TResponse&gt;(request, context.CancellationToken);</c>
///     for a code-first unary interface method. The <see cref="CancellationToken"/> is sourced via
///     <see cref="CallContextCancellationTokenSource"/> registered on the method — this frame never
///     hardcodes the property access itself.
/// </summary>
internal sealed class ForwardCodeFirstUnaryFrame : SyncFrame
{
    private readonly MethodInfo _rpc;
    private readonly InjectedField _busField;
    private Variable? _cancellationToken;

    public ForwardCodeFirstUnaryFrame(MethodInfo rpc, InjectedField busField)
    {
        _rpc = rpc;
        _busField = busField;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellationToken = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellationToken;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var parameters = _rpc.GetParameters();
        var requestName = parameters[0].Name ?? "arg0";
        var responseType = _rpc.ReturnType.GetGenericArguments()[0];

        var busInvoke =
            $"{_busField.Usage}.{nameof(IMessageBus.InvokeAsync)}<{responseType.FullNameInCode()}>({requestName}, {_cancellationToken!.Usage})";

        if (Next == null)
        {
            writer.Write($"return {busInvoke};");
        }
        else
        {
            // After-frames are present — await so they can run before the response is returned.
            writer.Write($"var result = await {busInvoke};");
            Next.GenerateCode(method, writer);
            writer.Write("return result;");
        }
    }
}

/// <summary>
///     Emits <c>return _bus.StreamAsync&lt;TResponse&gt;(request, context.CancellationToken);</c>
///     for a code-first server-streaming interface method. Because <see cref="IMessageBus.StreamAsync{T}"/>
///     returns <see cref="IAsyncEnumerable{T}"/> synchronously, no <c>async</c> modifier is needed and
///     this is a <see cref="SyncFrame"/>. The <see cref="CancellationToken"/> is resolved via
///     <see cref="CallContextCancellationTokenSource"/> — same composable pattern as the unary frame.
/// </summary>
internal sealed class ForwardCodeFirstServerStreamFrame : SyncFrame
{
    private readonly MethodInfo _rpc;
    private readonly InjectedField _busField;
    private Variable? _cancellationToken;

    public ForwardCodeFirstServerStreamFrame(MethodInfo rpc, InjectedField busField)
    {
        _rpc = rpc;
        _busField = busField;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellationToken = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellationToken;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var parameters = _rpc.GetParameters();
        var requestName = parameters[0].Name ?? "arg0";
        var responseType = _rpc.ReturnType.GetGenericArguments()[0];

        writer.Write(
            $"return {_busField.Usage}.{nameof(IMessageBus.StreamAsync)}<{responseType.FullNameInCode()}>({requestName}, {_cancellationToken!.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
