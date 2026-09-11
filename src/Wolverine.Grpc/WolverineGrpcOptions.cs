using System.Diagnostics;
using System.ServiceModel;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Grpc.MultiTenancy;
using Wolverine.Middleware;

namespace Wolverine.Grpc;

/// <summary>
///     Wolverine-side configuration for gRPC services. The gRPC counterpart to
///     <c>WolverineHttpOptions</c> — exposes a <see cref="MiddlewarePolicy"/> dedicated to
///     gRPC chains, a <see cref="Policies"/> list for structural chain customizations, and
///     server-side exception-to-status-code mappings. Middleware registered here targets gRPC
///     chains exclusively and does not leak through the global <c>opts.Policies.AddMiddleware</c>
///     path (which is intentionally <c>HandlerChain</c>-only).
/// </summary>
public sealed class WolverineGrpcOptions
{
    public WolverineGrpcOptions()
    {
        TenantIdDetection = new GrpcTenantIdDetection(this);
        Policies.Add(TenantIdDetection);
    }

    internal MiddlewarePolicy Middleware { get; } = new();

    internal GrpcTenantIdDetection TenantIdDetection { get; }

    /// <summary>
    ///     Configure server-side tenant id detection for Wolverine-managed gRPC services — the
    ///     gRPC counterpart to <c>WolverineHttpOptions.TenantId</c>. Strategies are tried in
    ///     registration order and the first non-empty tenant id wins; the detected value is
    ///     written to the <c>tenantId</c> code-generation variable consumed by Marten/Polecat
    ///     session-opening frames and applied to the scoped <see cref="IMessageBus"/> before the
    ///     RPC forwards to any Wolverine handler.
    ///     <para>
    ///         Zero-config default: when nothing is configured here and
    ///         <see cref="PropagateEnvelopeHeaders"/> is <c>true</c> (the default), Wolverine
    ///         detects the <c>tenant-id</c> metadata header that
    ///         <c>WolverineGrpcClientPropagationInterceptor</c> stamps on outgoing calls — so a
    ///         Wolverine-to-Wolverine hop round-trips the tenant id with no server configuration.
    ///     </para>
    /// </summary>
    public IGrpcTenantDetectionPolicies TenantId => TenantIdDetection;

    /// <summary>
    ///     Runs the configured tenant detection strategies against the current call and returns
    ///     the first non-empty tenant id found, or null. Called from generated gRPC service
    ///     wrappers; accepts null (a code-first <c>CallContext</c> outside a server call has no
    ///     <see cref="ServerCallContext"/>) and returns null in that case.
    /// </summary>
    public async ValueTask<string?> TryDetectTenantIdAsync(ServerCallContext? callContext)
    {
        if (callContext == null)
        {
            return null;
        }

        foreach (var strategy in TenantIdDetection.Strategies)
        {
            var tenantId = await strategy.DetectTenant(callContext);
            if (tenantId.IsNotEmpty())
            {
                Activity.Current?.SetTag(MetricsConstants.TenantIdKey, tenantId);
                return tenantId;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether <see cref="WolverineGrpcServicePropagationInterceptor"/> reads the
    ///     <c>correlation-id</c>/<c>tenant-id</c> envelope headers off inbound calls onto the
    ///     ambient <see cref="IMessageContext"/> before the service method runs. Defaults to
    ///     <c>true</c>. The client-side counterpart is <c>WolverineGrpcClientOptions.PropagateEnvelopeHeaders</c>.
    /// </summary>
    public bool PropagateEnvelopeHeaders { get; set; } = true;

    // Insertion-ordered and deduplicated by IncludeCodeFirstContract. Order matters for byte-stable
    // codegen output, and AddWolverineGrpc(configure) re-runs the callback on repeat calls, so the
    // same contract must be able to arrive more than once without producing two chains.
    private readonly List<Type> _codeFirstContracts = [];

    /// <summary>
    ///     Code-first <c>[ServiceContract]</c> interfaces registered through
    ///     <see cref="IncludeCodeFirstContract{T}"/> / <see cref="IncludeCodeFirstContract(Type)"/>.
    ///     Wolverine generates and maps an implementation for each of these exactly as it does for an
    ///     interface carrying <see cref="WolverineGrpcServiceAttribute"/>. Read by
    ///     <see cref="GrpcGraph.DiscoverServices"/>.
    /// </summary>
    public IReadOnlyList<Type> CodeFirstContracts => _codeFirstContracts;

    /// <summary>
    ///     Register a code-first gRPC service contract for Wolverine's generated-implementation path
    ///     without putting <see cref="WolverineGrpcServiceAttribute"/> on the interface (GH-4396).
    ///     <para>
    ///         Use this when the <c>[ServiceContract]</c> interface lives in a contracts assembly shared
    ///         with clients: the contracts project then needs only <c>protobuf-net.Grpc</c>, and the
    ///         server hosting stack (<c>WolverineFx.Grpc</c>, <c>Grpc.AspNetCore</c>, ...) stops flowing
    ///         to every client that binds the interface. The host names the contract here instead, the
    ///         same way <c>opts.Discovery.IncludeType&lt;T&gt;()</c> names a handler type the scan would
    ///         not otherwise find.
    ///     </para>
    ///     <para>
    ///         Everything downstream is identical to the attributed form: the same
    ///         <see cref="CodeFirstGrpcServiceChain"/>, the same generated
    ///         <c>{InterfaceNameWithoutLeadingI}GrpcHandler</c>, the same <c>TypeLoadMode.Static</c>
    ///         registry, and the same rule that a concrete <c>*GrpcService</c> class implementing the
    ///         contract is not also mapped. Bidirectional-streaming methods are skipped by generation,
    ///         so a contract that declares one will not compile on this path; keep such contracts on a
    ///         hand-written service class.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">A non-generic interface carrying <c>[ServiceContract]</c>.</typeparam>
    public WolverineGrpcOptions IncludeCodeFirstContract<T>() where T : class
        => IncludeCodeFirstContract(typeof(T));

    /// <summary>
    ///     Non-generic overload of <see cref="IncludeCodeFirstContract{T}"/> for contracts only known at
    ///     runtime. Validates eagerly so a bad registration fails at <c>AddWolverineGrpc</c> time with a
    ///     message naming the type, rather than at the first RPC.
    /// </summary>
    /// <param name="contractType">A non-generic interface carrying <c>[ServiceContract]</c>.</param>
    /// <exception cref="ArgumentException">
    ///     <paramref name="contractType"/> is not an interface, is an open generic, or does not carry
    ///     <c>[ServiceContract]</c>.
    /// </exception>
    public WolverineGrpcOptions IncludeCodeFirstContract(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        if (!contractType.IsInterface)
        {
            throw new ArgumentException(
                $"{contractType.FullNameInCode()} cannot be registered as a code-first gRPC contract because it is not an interface. "
                + "Wolverine generates the implementation of a [ServiceContract] interface; a concrete service class is discovered "
                + "by the 'GrpcService' name suffix or [WolverineGrpcService] instead.",
                nameof(contractType));
        }

        if (contractType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"{contractType.FullNameInCode()} cannot be registered as a code-first gRPC contract because it is an open generic interface. "
                + "Register a closed interface type.",
                nameof(contractType));
        }

        if (!contractType.IsDefined(typeof(ServiceContractAttribute), inherit: false))
        {
            throw new ArgumentException(
                $"{contractType.FullNameInCode()} cannot be registered as a code-first gRPC contract because it does not carry "
                + "[System.ServiceModel.ServiceContract]. protobuf-net.Grpc needs that attribute to route the service; "
                + "add it to the interface (it comes from protobuf-net.Grpc, not from WolverineFx.Grpc).",
                nameof(contractType));
        }

        if (!_codeFirstContracts.Contains(contractType))
        {
            _codeFirstContracts.Add(contractType);
        }

        return this;
    }

    /// <summary>
    ///     Structural policies applied to all discovered gRPC chains during bootstrapping.
    ///     Analogous to <c>WolverineHttpOptions.Policies</c> — use when you need typed access
    ///     to chain properties beyond what <see cref="AddMiddleware{T}(Func{IChain,bool}?)"/>
    ///     provides (e.g., inspecting <see cref="GrpcServiceChain.ProtoServiceName"/> or
    ///     <see cref="HandWrittenGrpcServiceChain.ServiceContractType"/>).
    /// </summary>
    public List<IGrpcChainPolicy> Policies { get; } = [];

    /// <summary>
    ///     Register an <see cref="IGrpcChainPolicy"/> by type using its default constructor.
    /// </summary>
    public WolverineGrpcOptions AddPolicy<T>() where T : IGrpcChainPolicy, new()
    {
        Policies.Add(new T());
        return this;
    }

    /// <summary>
    ///     Register an <see cref="IGrpcChainPolicy"/> instance directly.
    /// </summary>
    public WolverineGrpcOptions AddPolicy(IGrpcChainPolicy policy)
    {
        Policies.Add(policy);
        return this;
    }

    /// <summary>
    ///     Apply an ASP.NET endpoint convention to every Wolverine-managed gRPC chain — proto-first,
    ///     code-first and hand-written alike. The gRPC counterpart to
    ///     <c>WolverineHttpOptions.ConfigureEndpoints</c> (GH-4383).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The lambda takes <see cref="IGrpcChain"/> because the three chain kinds close
    ///         <c>Chain&lt;TSelf, TAttribute&gt;</c> over different attribute types and share no ancestor
    ///         of their own. <see cref="IGrpcChain.ServiceType"/> is the type the chain was built from,
    ///         so a convention can target one service; per-RPC targeting needs nothing new, because
    ///         every gRPC endpoint already carries <c>GrpcMethodMetadata</c> and a convention can match
    ///         on <c>Method.Name</c> without any route-string matching.
    ///     </para>
    ///     <para>
    ///         ⚠️ This reaches the chains Wolverine <em>generates</em> a type for. A hand-written service
    ///         class that Wolverine maps directly — one with no <c>HandWrittenGrpcServiceChain</c> — has
    ///         no chain to configure and is untouched; put conventions on it where you map it.
    ///     </para>
    /// </remarks>
    /// <param name="configure">The convention, applied to each chain before its endpoint is mapped.</param>
    public WolverineGrpcOptions ConfigureEndpoints(Action<IGrpcChain> configure)
    {
        return AddPolicy(new LambdaGrpcChainPolicy((protoFirst, codeFirst, handWritten, _, _) =>
        {
            foreach (var chain in protoFirst) configure(chain);
            foreach (var chain in codeFirst) configure(chain);
            foreach (var chain in handWritten) configure(chain);
        }));
    }

    /// <summary>
    ///     Equivalent of calling <c>RequireAuthorization()</c> on every Wolverine-managed gRPC endpoint,
    ///     mirroring <c>WolverineHttpOptions.RequireAuthorizeOnAll</c>.
    /// </summary>
    /// <remarks>
    ///     This puts the policy on the <em>endpoint</em>, so ASP.NET's <c>AuthorizationMiddleware</c>
    ///     is the enforcer and a fallback policy still backs it up. A gRPC <c>Interceptor</c> or
    ///     Wolverine middleware cannot do that job: both run inside the generated method, long after
    ///     the router has decided.
    /// </remarks>
    /// <param name="policyNames">Authorization policy names, or none for the default policy.</param>
    public WolverineGrpcOptions RequireAuthorizeOnAll(params string[] policyNames)
    {
        return policyNames.Length == 0
            ? ConfigureEndpoints(c => c.RequireAuthorization())
            : ConfigureEndpoints(c => c.RequireAuthorization(policyNames));
    }


    // Ordered list so the most-recently-registered entry wins on overlap;
    // we walk it in reverse so callers can add more-specific entries after generic ones.
    private readonly List<(Type ExceptionType, StatusCode StatusCode)> _exceptionMappings = [];

    /// <summary>
    ///     Register a middleware type that will be applied to all Wolverine-managed gRPC chains
    ///     (proto-first, code-first generated, and hand-written) unless <paramref name="filter"/>
    ///     excludes a specific chain.
    /// </summary>
    /// <param name="filter">
    ///     Optional predicate over <see cref="IChain"/>. When null, middleware is applied to every
    ///     gRPC chain. Pattern-match on the concrete type to filter by chain kind, e.g.
    ///     <c>c => c is GrpcServiceChain g &amp;&amp; g.ProtoServiceName == "Greeter"</c>.
    /// </param>
    /// <typeparam name="T">The middleware class (looked up by convention for <c>Before</c>/<c>After</c>/<c>Finally</c> methods).</typeparam>
    public void AddMiddleware<T>(Func<IChain, bool>? filter = null)
        => AddMiddleware(typeof(T), filter);

    /// <summary>
    ///     Register a middleware type that will be applied to all Wolverine-managed gRPC chains
    ///     unless <paramref name="filter"/> excludes a specific chain.
    /// </summary>
    /// <param name="middlewareType">The middleware class.</param>
    /// <param name="filter">
    ///     Optional predicate. When null, defaults to matching every Wolverine gRPC chain —
    ///     proto-first, code-first, and hand-written. See
    ///     <see cref="AddMiddleware{T}(Func{IChain,bool}?)"/> for details.
    /// </param>
    public void AddMiddleware(Type middlewareType, Func<IChain, bool>? filter = null)
    {
        Middleware.AddType(middlewareType, filter ?? IsGrpcChain);
    }

    /// <summary>
    ///     Default chain predicate: matches every Wolverine gRPC chain type.
    /// </summary>
    private static bool IsGrpcChain(IChain chain)
        => chain is GrpcServiceChain or CodeFirstGrpcServiceChain or HandWrittenGrpcServiceChain;

    /// <summary>
    ///     Override the server-side <see cref="StatusCode"/> returned for a specific exception type.
    ///     Consulted after the opt-in <c>google.rpc.Status</c> rich-error pipeline and before the
    ///     built-in default table, so application-specific mappings always win over the defaults.
    ///     Inheritance is respected: a mapping for <c>MyBaseException</c> also matches
    ///     <c>MyDerivedException</c> unless a more-specific mapping exists.
    /// </summary>
    /// <typeparam name="TException">The exception type to intercept.</typeparam>
    /// <param name="statusCode">The gRPC status code to return when <typeparamref name="TException"/> is thrown.</param>
    public WolverineGrpcOptions MapException<TException>(StatusCode statusCode)
        where TException : Exception
        => MapException(typeof(TException), statusCode);

    /// <summary>
    ///     Override the server-side <see cref="StatusCode"/> for a specific exception type.
    ///     Non-generic overload for cases where the exception type is only known at runtime.
    /// </summary>
    /// <param name="exceptionType">Must be assignable to <see cref="Exception"/>.</param>
    /// <param name="statusCode">The gRPC status code to return.</param>
    public WolverineGrpcOptions MapException(Type exceptionType, StatusCode statusCode)
    {
        if (!typeof(Exception).IsAssignableFrom(exceptionType))
            throw new ArgumentException($"{exceptionType.FullName} must be assignable to Exception.", nameof(exceptionType));

        _exceptionMappings.Add((exceptionType, statusCode));
        return this;
    }

    /// <summary>
    ///     Returns the user-registered <see cref="StatusCode"/> for the given exception, walking the
    ///     exception's inheritance chain from most-derived to least-derived. Later registrations win
    ///     over earlier ones for the same type. Returns <c>null</c> when no mapping matches so the
    ///     caller can fall through to the built-in default table.
    /// </summary>
    internal StatusCode? TryMapException(Exception exception)
    {
        if (_exceptionMappings.Count == 0) return null;

        var type = exception.GetType();
        while (type != null && type != typeof(object))
        {
            // Walk registrations in reverse — last registration for a given type wins
            for (var i = _exceptionMappings.Count - 1; i >= 0; i--)
            {
                if (_exceptionMappings[i].ExceptionType == type)
                    return _exceptionMappings[i].StatusCode;
            }

            type = type.BaseType;
        }

        return null;
    }
}
