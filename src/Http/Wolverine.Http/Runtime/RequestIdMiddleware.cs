using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime;

namespace Wolverine.Http.Runtime;

#region sample_RequestIdMiddleware

public static class RequestIdMiddleware
{
    public const string CorrelationIdHeaderKey = "X-Correlation-ID";

    // Remember that most Wolverine middleware can be done with "just" a method
    public static void Apply(HttpContext httpContext, IMessageContext messaging)
    {
        if (httpContext.Request.Headers.TryGetValue(CorrelationIdHeaderKey, out var correlationId))
        {
            messaging.CorrelationId = correlationId.First();
        }
    }
}

#endregion

#region sample_RequestIdPolicy

internal class RequestIdPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var serviceDependencies = chain.ServiceDependencies(container, Type.EmptyTypes).ToArray();
            if (serviceDependencies.Contains(typeof(IMessageContext)) ||
                serviceDependencies.Contains(typeof(IMessageBus)))
            {
                chain.Middleware.Insert(0, new MethodCall(typeof(RequestIdMiddleware), nameof(RequestIdMiddleware.Apply)));
            }
        }
    }
}

#endregion

internal class MyAuthenticationMiddleware;

// Leave this alone, it's used in sample code for docs
internal class RequestIdPolicyApplication
{
    public void bootstrap(WebApplication app)
    {
        #region sample_adding_http_policy

        // app is a WebApplication
        app.MapWolverineEndpoints(opts =>
        {
            // add the policy to Wolverine HTTP endpoints
            opts.AddPolicy<RequestIdPolicy>();
        });

        #endregion

        #region sample_simple_middleware_policy_for_http

        app.MapWolverineEndpoints(opts =>
        {
            // Fake policy to add authentication middleware to any endpoint classes under
            // an application namespace
            opts.AddMiddleware(typeof(MyAuthenticationMiddleware),
                c => c.HandlerCalls().Any(x => x.HandlerType.IsInNamespace("MyApp.Authenticated")));
        });

        #endregion
    }
}