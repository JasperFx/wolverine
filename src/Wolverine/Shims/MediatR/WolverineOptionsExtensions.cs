using Wolverine.Shims.MediatR;

namespace Wolverine.Shims;

/// <summary>
/// Extension methods for using MediatR-style handlers in Wolverine
/// </summary>
public static class WolverineOptionsExtensions
{
    /// <summary>
    /// Enables discovery of handlers implementing Wolverine's MediatR shim interfaces
    /// (IRequestHandler&lt;TRequest, TResponse&gt; and IRequestHandler&lt;TRequest&gt;).
    /// This allows you to use MediatR-style handler patterns without depending on the MediatR library.
    /// </summary>
    public static WolverineOptions UseMediatRHandlers(this WolverineOptions options)
    {
        options.Discovery.CustomizeHandlerDiscovery(query =>
        {
            query.Includes.Implements(typeof(IRequestHandler<>));
            query.Includes.Implements(typeof(IRequestHandler<,>));
        });
        
        return options;
    }
}
