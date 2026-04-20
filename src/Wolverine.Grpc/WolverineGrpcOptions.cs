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
}
