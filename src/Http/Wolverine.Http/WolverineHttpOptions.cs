using System.Text.Json;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Middleware;

namespace Wolverine.Http;

[Singleton]
public class WolverineHttpOptions
{
    internal JsonSerializerOptions JsonSerializerOptions { get; set; } = new();
    internal EndpointGraph? Endpoints { get; set; }

    internal MiddlewarePolicy Middleware { get; } = new();

    public List<IEndpointPolicy> Policies { get; } = new();

    /// <summary>
    /// Add a new IEndpointPolicy for the Wolverine endpoints
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddPolicy<T>() where T : IEndpointPolicy, new()
    {
        Policies.Add(new T());
    }

    /// <summary>
    /// Apply user-defined customizations to how endpoints are handled
    /// by Wolverine
    /// </summary>
    /// <param name="configure"></param>
    public void ConfigureEndpoints(Action<EndpointChain> configure)
    {
        var policy = new LambdaEndpointPolicy((c, _, _) => configure(c));
        Policies.Add(policy);
    }

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    public void AddMiddlewareByMessageType(Type middlewareType)
    {
        Middleware.AddType(middlewareType, chain => chain is EndpointChain);
    }

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    /// <typeparam name="T">The actual middleware type</typeparam>
    public void AddMiddleware<T>(Func<EndpointChain, bool>? filter = null)
    {
        AddMiddleware(typeof(T), filter);
    }

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="middlewareType">The actual middleware type</param>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    public void AddMiddleware(Type middlewareType, Func<EndpointChain, bool>? filter = null)
    {
        Func<IChain, bool> chainFilter = c => c is EndpointChain;
        if (filter != null)
        {
            chainFilter = c => c is EndpointChain e && filter!(e);
        }
        
        Middleware.AddType(middlewareType, chainFilter);
    }

}

