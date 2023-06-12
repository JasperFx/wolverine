using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Lamar;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Runtime;

public static class RequestIdMiddleware
{
    public const string CorrelationIdHeaderKey = "X-Correlation-ID";
    
    public static void Apply(HttpContext httpContext, IMessageContext messaging)
    {
        if (httpContext.Request.Headers.TryGetValue(CorrelationIdHeaderKey, out var correlationId))
        {
            messaging.CorrelationId = correlationId.First();
        }
    }
}

internal class RequestIdPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
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