using Grpc.Core;
using Wolverine.Configuration;
using Wolverine.Middleware;

namespace Wolverine.Grpc;

/// <summary>
///     Wolverine-side configuration for proto-first gRPC services. The gRPC counterpart to
///     <c>WolverineHttpOptions</c> — exposes a <see cref="MiddlewarePolicy"/> dedicated to
///     <see cref="GrpcServiceChain"/>s so that policy-registered middleware can target gRPC
///     services without leaking through the global <c>opts.Policies.AddMiddleware</c> path
///     (which is intentionally <c>HandlerChain</c>-only).
/// </summary>
public sealed class WolverineGrpcOptions
{
    internal MiddlewarePolicy Middleware { get; } = new();

    // Ordered list so the most-recently-registered entry wins on overlap;
    // we walk it in reverse so callers can add more-specific entries after generic ones.
    private readonly List<(Type ExceptionType, StatusCode StatusCode)> _exceptionMappings = [];

    /// <summary>
    ///     Register a middleware type that will be applied to every <see cref="GrpcServiceChain"/>
    ///     unless <paramref name="filter"/> excludes it.
    /// </summary>
    /// <param name="filter">Optional predicate restricting which gRPC service chains receive the middleware.</param>
    /// <typeparam name="T">The middleware class (looked up by convention for <c>Before</c>/<c>After</c>/<c>Finally</c> methods).</typeparam>
    public void AddMiddleware<T>(Func<GrpcServiceChain, bool>? filter = null)
        => AddMiddleware(typeof(T), filter);

    /// <summary>
    ///     Register a middleware type that will be applied to every <see cref="GrpcServiceChain"/>
    ///     unless <paramref name="filter"/> excludes it.
    /// </summary>
    /// <param name="middlewareType">The middleware class.</param>
    /// <param name="filter">Optional predicate restricting which gRPC service chains receive the middleware.</param>
    public void AddMiddleware(Type middlewareType, Func<GrpcServiceChain, bool>? filter = null)
    {
        Func<IChain, bool> chainFilter = c => c is GrpcServiceChain;
        if (filter != null)
        {
            chainFilter = c => c is GrpcServiceChain g && filter(g);
        }

        Middleware.AddType(middlewareType, chainFilter);
    }

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
