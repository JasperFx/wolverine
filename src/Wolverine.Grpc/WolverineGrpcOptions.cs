using Grpc.Core;
using Wolverine.Configuration;
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
    internal MiddlewarePolicy Middleware { get; } = new();

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

    // Ordered list so the most-recently-registered entry wins on overlap;
    // we walk it in reverse so callers can add more-specific entries after generic ones.
    private readonly List<(Type ExceptionType, StatusCode StatusCode)> _exceptionMappings = [];

    /// <summary>
    ///     Register a middleware type that will be applied to all Wolverine-managed gRPC chains
    ///     (proto-first and hand-written) unless <paramref name="filter"/> excludes a specific chain.
    ///     Code-first chains (<see cref="CodeFirstGrpcServiceChain"/>) are excluded until they gain
    ///     full <c>Chain&lt;&gt;</c> parity (P3).
    /// </summary>
    /// <param name="filter">
    ///     Optional predicate over <see cref="IChain"/>. When null, middleware is applied to every
    ///     proto-first (<see cref="GrpcServiceChain"/>) and hand-written
    ///     (<see cref="HandWrittenGrpcServiceChain"/>) gRPC chain. Pattern-match on the concrete
    ///     type to filter by chain kind, e.g.
    ///     <c>c => c is GrpcServiceChain g &amp;&amp; g.ProtoServiceName == "Greeter"</c>.
    /// </param>
    /// <typeparam name="T">The middleware class (looked up by convention for <c>Before</c>/<c>After</c>/<c>Finally</c> methods).</typeparam>
    public void AddMiddleware<T>(Func<IChain, bool>? filter = null)
        => AddMiddleware(typeof(T), filter);

    /// <summary>
    ///     Register a middleware type that will be applied to all Wolverine-managed gRPC chains
    ///     (proto-first and hand-written) unless <paramref name="filter"/> excludes a specific chain.
    ///     Code-first chains are excluded until P3.
    /// </summary>
    /// <param name="middlewareType">The middleware class.</param>
    /// <param name="filter">
    ///     Optional predicate. When null, defaults to matching every proto-first and hand-written
    ///     gRPC chain. See <see cref="AddMiddleware{T}(Func{IChain,bool}?)"/> for details.
    /// </param>
    public void AddMiddleware(Type middlewareType, Func<IChain, bool>? filter = null)
    {
        Middleware.AddType(middlewareType, filter ?? IsGrpcChain);
    }

    /// <summary>
    ///     Default chain predicate: matches every Wolverine gRPC chain type that participates in
    ///     the <c>Chain&lt;&gt;</c> middleware pipeline today (proto-first and hand-written).
    ///     Code-first chains are excluded pending P3 parity.
    /// </summary>
    private static bool IsGrpcChain(IChain chain)
        => chain is GrpcServiceChain or HandWrittenGrpcServiceChain;

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
