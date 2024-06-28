using System.Text.Json;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Policies;
using Wolverine.Http.Resources;
using Wolverine.Http.Runtime;
using Wolverine.Http.Runtime.MultiTenancy;
using Wolverine.Middleware;

namespace Wolverine.Http;

public enum JsonUsage
{
    SystemTextJson,
    NewtonsoftJson
}

public interface ITenantDetectionPolicies
{
    /// <summary>
    /// Try to detect the tenant id from the named route argument
    /// </summary>
    /// <param name="routeArgumentName"></param>
    void IsRouteArgumentNamed(string routeArgumentName);

    /// <summary>
    /// Try to detect the tenant id from an expected query string value
    /// </summary>
    /// <param name="key"></param>
    void IsQueryStringValue(string key);

    /// <summary>
    /// Try to detect the tenant id from a request header
    /// if it exists
    /// </summary>
    /// <param name="headerKey"></param>
    void IsRequestHeaderValue(string headerKey);

    /// <summary>
    /// Try to detect the tenant id from the ClaimsPrincipal for the
    /// current request
    /// </summary>
    /// <param name="claimType"></param>
    void IsClaimTypeNamed(string claimType);


    /// <summary>
    /// Simplistic tenant id detection that uses the sub domain name of the current
    /// request location as the tenant id
    /// </summary>
    void IsSubDomainName();

    /// <summary>
    /// Assert that the tenant id was successfully detected, and if no tenant id
    /// is found, return a ProblemDetails with a 400 status code
    /// </summary>
    void AssertExists();

    /// <summary>
    /// Register a custom tenant detection strategy. Be aware though, this object
    /// will be resolved from your application container, but will be done as with Singleton
    /// scoping.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void DetectWith<T>() where T : ITenantDetection;


    /// <summary>
    /// Register a custom tenant detection strategy
    /// </summary>
    /// <param name="detection"></param>
    void DetectWith(ITenantDetection detection);

    /// <summary>
    /// If no tenant id is detected, this value should be used for the Tenant Id
    /// </summary>
    /// <param name="defaultTenantId"></param>
    void DefaultIs(string defaultTenantId);
}

public class WolverineHttpOptions
{
    public WolverineHttpOptions()
    {
        Policies.Add(new HttpAwarePolicy());
        Policies.Add(new RequestIdPolicy());
        Policies.Add(new RequiredEntityPolicy());

        Policies.Add(TenantIdDetection);
    }
    
    public async ValueTask<string?> TryDetectTenantId(HttpContext httpContext)
    {
        foreach (var strategy in TenantIdDetection.Strategies)
        {
            var tenantId = await strategy.DetectTenant(httpContext);
            if (tenantId.IsNotEmpty()) return tenantId;
        }

        return null;
    }
    
    public string? TryDetectTenantIdSynchronously(HttpContext httpContext)
    {
        return TenantIdDetection
            .Strategies
            .OfType<ISynchronousTenantDetection>()
            .Select(strategy => strategy.DetectTenantSynchronously(httpContext))
            .FirstOrDefault(tenantId => tenantId.IsNotEmpty());
    }

    internal TenantIdDetection TenantIdDetection { get; } = new();

    internal Lazy<JsonSerializerOptions> JsonSerializerOptions { get; set; } = new(() => new JsonSerializerOptions());

    internal JsonSerializerSettings NewtonsoftSerializerSettings { get; set; } = new();

    internal HttpGraph? Endpoints { get; set; }

    internal MiddlewarePolicy Middleware { get; } = new();

    public List<IHttpPolicy> Policies { get; } = new();

    public List<IResourceWriterPolicy> ResourceWriterPolicies { get; } = new();

    /// <summary>
    /// Configure built in tenant id detection strategies
    /// </summary>
    public ITenantDetectionPolicies TenantId => TenantIdDetection;

    /// <summary>
    /// Opt into using Newtonsoft.Json for all JSON serialization in the Wolverine
    /// Http handlers
    /// </summary>
    /// <param name="configure"></param>
    public void UseNewtonsoftJsonForSerialization(Action<JsonSerializerSettings>? configure = null)
    {
        configure?.Invoke(NewtonsoftSerializerSettings);
        Endpoints.UseNewtonsoftJson();

    }

    /// <summary>
    ///     Customize Wolverine's handling of parameters to HTTP endpoint methods
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddParameterHandlingStrategy<T>() where T : IParameterStrategy, new()
    {
        AddParameterHandlingStrategy(new T());
    }

    /// <summary>
    ///     Customize Wolverine's handling of parameters to HTTP endpoint methods
    /// </summary>
    /// <param name="strategy"></param>
    /// <exception cref="NotImplementedException"></exception>
    public void AddParameterHandlingStrategy(IParameterStrategy strategy)
    {
        Endpoints!.InsertParameterStrategy(strategy);
    }

    #region sample_RequireAuthorizeOnAll

    /// <summary>
    /// Equivalent of calling RequireAuthorization() on all wolverine endpoints
    /// </summary>
    public void RequireAuthorizeOnAll()
    {
        ConfigureEndpoints(e => e.RequireAuthorization());
    }

    #endregion

    /// <summary>
    ///     Add a new IEndpointPolicy for the Wolverine endpoints
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddPolicy<T>() where T : IHttpPolicy, new()
    {
        Policies.Add(new T());
    }

    /// <summary>
    ///     Add a new IResourceWriterPolicy for the Wolverine endpoints
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddResourceWriterPolicy<T>() where T : IResourceWriterPolicy, new()
    {
        ResourceWriterPolicies.Add(new T());
    }

    /// <summary>
    ///     Add a new IResourceWriterPolicy for the Wolverine endpoints
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddResourceWriterPolicy<T>(T policy) where T : IResourceWriterPolicy
    {
        ResourceWriterPolicies.Add(policy);
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
            chainFilter = c => c is HttpChain e && filter(e);
        }

        Middleware.AddType(middlewareType, chainFilter);
    }

    /// <summary>
    ///     From this url, forward a JSON serialized message by publishing through Wolverine
    /// </summary>
    /// <param name="httpMethod"></param>
    /// <param name="url"></param>
    /// <param name="customize">Optionally customize the HttpChain handling for elements like validation</param>
    /// <typeparam name="T"></typeparam>
    public RouteHandlerBuilder PublishMessage<T>(HttpMethod httpMethod, string url, Action<HttpChain>? customize = null)
    {
#pragma warning disable CS4014
        var method = MethodCall.For<PublishingEndpoint<T>>(x => x.PublishAsync(default!, null!, null!));
#pragma warning restore CS4014
        var chain = Endpoints!.Add(method, httpMethod, url);

        chain.MapToRoute(httpMethod.ToString(), url);
        chain.DisplayName = $"Forward {typeof(T).FullNameInCode()} to Wolverine";
        chain.OperationId = $"Publish:{typeof(T).FullNameInCode()}";
        customize?.Invoke(chain);

        return chain.Metadata;
    }

    public RouteHandlerBuilder PublishMessage<T>(string url, Action<HttpChain>? customize = null)
    {
        return PublishMessage<T>(HttpMethod.Post, url, customize);
    }

    /// <summary>
    ///     From this url, forward a JSON serialized message by sending through Wolverine
    /// </summary>
    /// <param name="httpMethod"></param>
    /// <param name="url"></param>
    /// <param name="customize">Optionally customize the HttpChain handling for elements like validation</param>
    /// <typeparam name="T"></typeparam>
    public RouteHandlerBuilder SendMessage<T>(HttpMethod httpMethod, string url, Action<HttpChain>? customize = null)
    {
#pragma warning disable CS4014
        var method = MethodCall.For<SendingEndpoint<T>>(x => x.SendAsync(default!, null!, null!));
#pragma warning restore CS4014
        var chain = Endpoints!.Add(method, httpMethod, url);

        chain.MapToRoute(httpMethod.ToString(), url);
        chain.DisplayName = $"Forward {typeof(T).FullNameInCode()} to Wolverine";
        chain.OperationId = $"Send:{typeof(T).FullNameInCode()}";
        customize?.Invoke(chain);

        return chain.Metadata;
    }

    public RouteHandlerBuilder SendMessage<T>(string url, Action<HttpChain>? customize = null)
    {
        return SendMessage<T>(HttpMethod.Post, url, customize);
    }
}