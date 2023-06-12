using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Runtime;
using Wolverine.Middleware;

namespace Wolverine.Http;

[Singleton]
public class WolverineHttpOptions
{
    public WolverineHttpOptions()
    {
        Policies.Add(new HttpAwarePolicy());
        Policies.Add(new RequestIdPolicy());
    }

    internal JsonSerializerOptions JsonSerializerOptions { get; set; } = new();
    internal HttpGraph? Endpoints { get; set; }

    internal MiddlewarePolicy Middleware { get; } = new();

    public List<IHttpPolicy> Policies { get; } = new();

    /// <summary>
    /// Customize Wolverine's handling of parameters to HTTP endpoint methods
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddParameterHandlingStrategy<T>() where T : IParameterStrategy, new()
    {
        AddParameterHandlingStrategy(new T());
    }

    /// <summary>
    /// Customize Wolverine's handling of parameters to HTTP endpoint methods
    /// </summary>
    /// <param name="strategy"></param>
    /// <exception cref="NotImplementedException"></exception>
    public void AddParameterHandlingStrategy(IParameterStrategy strategy)
    {
        Endpoints.InsertParameterStrategy(strategy);
    }


    /// <summary>
    ///     Add a new IEndpointPolicy for the Wolverine endpoints
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddPolicy<T>() where T : IHttpPolicy, new()
    {
        Policies.Add(new T());
    }

    /// <summary>
    ///     Apply user-defined customizations to how endpoints are handled
    ///     by Wolverine
    /// </summary>
    /// <param name="configure"></param>
    public void ConfigureEndpoints(Action<HttpChain> configure)
    {
        var policy = new LambdaHttpPolicy((c, _, _) => configure(c));
        Policies.Add(policy);
    }

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    public void AddMiddlewareByMessageType(Type middlewareType)
    {
        Middleware.AddType(middlewareType, chain => chain is HttpChain).MatchByMessageType = true;
    }

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    /// <typeparam name="T">The actual middleware type</typeparam>
    public void AddMiddleware<T>(Func<HttpChain, bool>? filter = null)
    {
        AddMiddleware(typeof(T), filter);
    }

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="middlewareType">The actual middleware type</param>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    public void AddMiddleware(Type middlewareType, Func<HttpChain, bool>? filter = null)
    {
        Func<IChain, bool> chainFilter = c => c is HttpChain;
        if (filter != null)
        {
            chainFilter = c => c is HttpChain e && filter!(e);
        }

        Middleware.AddType(middlewareType, chainFilter);
    }

    /// <summary>
    /// From this url, forward a JSON serialized message by publishing through Wolverine
    /// </summary>
    /// <param name="httpMethod"></param>
    /// <param name="url"></param>
    /// <param name="customize">Optionally customize the HttpChain handling for elements like validation</param>
    /// <typeparam name="T"></typeparam>
    public void PublishMessage<T>(HttpMethod httpMethod, string url, Action<HttpChain>? customize = null)
    {
        var method = MethodCall.For<PublishingEndpoint<T>>(x => x.PublishAsync(default, null, null));
        var chain = Endpoints.Add(method, httpMethod, url);

        chain.MapToRoute(httpMethod.ToString(), url);
        chain.DisplayName = $"Forward {typeof(T).FullNameInCode()} to Wolverine";
        customize?.Invoke(chain);
    }

    public void PublishMessage<T>(string url, Action<HttpChain>? customize = null)
    {
        PublishMessage<T>(HttpMethod.Post, url, customize);
    }
}